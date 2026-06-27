using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Voxa.Speech.Voxtral;

/// <summary>
/// Fully-offline streaming <see cref="ISpeechToTextEngine"/> backed by Mistral's open-weights
/// <b>Voxtral-Mini-4B-Realtime</b> served locally by vLLM over its realtime WebSocket API (VLS-009).
///
/// <para>Implemented <b>directly</b> against the contract rather than via <see cref="WebSocketSttEngine"/>:
/// vLLM's realtime protocol carries audio as base64-in-JSON (<c>input_audio_buffer.append</c>) instead of the
/// base's binary frames, and finalizes via an explicit <c>commit</c>→<c>done</c> round-trip (exactly one
/// <c>done</c> per utterance) rather than the base's local accumulator-flush — so there is no late-final
/// "bleed" to guard against and a plain <c>Channel&lt;TranscriptionResult&gt;</c> (à la the Mistral REST
/// engine) is the simpler, correct tool. Running deltas form the interim; <c>done</c> carries the final.</para>
/// </summary>
public sealed class VoxtralRealtimeSttEngine : ISpeechToTextEngine
{
    private readonly VoxtralOptions _options;
    private readonly IVoxtralServer _server;
    private readonly ILogger _logger;
    private readonly Channel<TranscriptionResult> _transcripts = Channel.CreateUnbounded<TranscriptionResult>();

    // Accumulates transcription.delta text for the current utterance into the interim shown live. Touched ONLY by
    // the single receive loop (delta append, done reset) — never cross-thread — so it needs no lock.
    private readonly StringBuilder _running = new();

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;

    /// <summary>Production: owns a managed (or connect-only) vLLM realtime server for the session's lifetime.</summary>
    public VoxtralRealtimeSttEngine(VoxtralOptions options, ILogger logger)
        : this(options, new VoxtralServerProcess(options, logger), logger) { }

    /// <summary>Test seam: inject a fake <see cref="IVoxtralServer"/> (e.g. one returning an in-process fake
    /// <c>/v1/realtime</c> endpoint) so the engine is exercisable with no vLLM, GPU, or model.</summary>
    internal VoxtralRealtimeSttEngine(VoxtralOptions options, IVoxtralServer server, ILogger logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken ct)
    {
        if (_ws is not null) return; // idempotent

        // Managed mode launches the vLLM server and polls it to readiness (a cold 4B load is slow); connect-only
        // just resolves the endpoint. Either way we connect only once the server is ready.
        var endpoint = await _server.StartAsync(ct).ConfigureAwait(false);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var ws = new ClientWebSocket();
        _logger.LogDebug("Voxtral connecting to {Endpoint}", endpoint);
        try
        {
            await ws.ConnectAsync(endpoint, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException)
        {
            ws.Dispose();
            throw new VoxaModelUnavailableException(
                $"Could not connect to the Voxtral vLLM realtime server at {endpoint}. In connect-only mode, ensure " +
                "your vLLM server is running; in managed mode, check the server logs (it may still be loading the model).", ex);
        }

        // Handshake: tell the server which model this session uses, before any audio flows.
        await ws.SendAsync(VoxtralWire.SessionUpdate(_options.Model), WebSocketMessageType.Text, endOfMessage: true, ct)
            .ConfigureAwait(false);
        _ws = ws;
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    public async ValueTask WriteAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open || pcm.IsEmpty) return;

        var append = VoxtralWire.AppendAudio(pcm.Span); // base64-encode synchronously before the await
        try
        {
            await ws.SendAsync(append, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException
                                      or OperationCanceledException or InvalidOperationException)
        {
            // Socket dropped / closing / disposed — drop this chunk rather than fault the pipeline.
        }
    }

    public IAsyncEnumerable<TranscriptionResult> ReadTranscriptsAsync(CancellationToken ct)
        => _transcripts.Reader.ReadAllAsync(ct);

    /// <summary>VAD speech-end: commit the buffered audio so the server finalizes this utterance and emits
    /// <c>transcription.done</c>. The final flows from the receive loop when <c>done</c> arrives — never emitted
    /// locally — so there is exactly one final per utterance with no post-speech round-trip the pipeline must wait on.</summary>
    public async Task FlushAsync()
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open) return;
        try
        {
            await ws.SendAsync(VoxtralWire.Commit(), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException
                                      or OperationCanceledException or InvalidOperationException)
        {
            // Socket gone — nothing left to commit.
        }
    }

    public async Task StopAsync()
    {
        var ws = _ws;
        if (ws is not null && ws.State == WebSocketState.Open)
        {
            try
            {
                using var close = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "stop", close.Token).ConfigureAwait(false);
            }
            catch { /* best-effort graceful close */ }
        }
        _cts?.Cancel();
        if (_receiveLoop is not null)
        {
            try { await _receiveLoop.ConfigureAwait(false); } catch { /* shutdown */ }
        }
        _transcripts.Writer.TryComplete();
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var ws = _ws!;
        var buffer = new byte[16 * 1024];
        using var msg = new MemoryStream();
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                ValueWebSocketReceiveResult result;
                try { result = await ws.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (WebSocketException) { break; }

                if (result.MessageType == WebSocketMessageType.Close) break;
                msg.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;

                if (result.MessageType == WebSocketMessageType.Text)
                    Ingest(Encoding.UTF8.GetString(msg.GetBuffer(), 0, (int)msg.Length));
                msg.SetLength(0);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        finally
        {
            _transcripts.Writer.TryComplete();
        }
    }

    // Pure dispatch of one parsed server frame; runs only on the receive loop, so _running is single-threaded here.
    private void Ingest(string json)
    {
        if (!VoxtralWire.TryParseServerMessage(json, out var m)) return;
        switch (m.Kind)
        {
            case VoxtralServerEvent.Delta:
                if (m.Text.Length == 0) return;
                _running.Append(m.Text);
                _transcripts.Writer.TryWrite(new TranscriptionResult(_running.ToString(), IsFinal: false, _options.Language));
                break;

            case VoxtralServerEvent.Done:
                // Prefer the server's authoritative full text; fall back to the accumulated deltas if it's empty.
                var text = m.Text.Length > 0 ? m.Text : _running.ToString();
                _running.Clear();
                if (!string.IsNullOrWhiteSpace(text))
                    _transcripts.Writer.TryWrite(new TranscriptionResult(text, IsFinal: true, _options.Language));
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_receiveLoop is not null)
        {
            try { await _receiveLoop.ConfigureAwait(false); } catch { /* shutdown */ }
        }
        _ws?.Dispose();
        _cts?.Dispose();
        _transcripts.Writer.TryComplete();
        await _server.DisposeAsync().ConfigureAwait(false); // managed mode: kill the server process tree
        GC.SuppressFinalize(this);
    }
}
