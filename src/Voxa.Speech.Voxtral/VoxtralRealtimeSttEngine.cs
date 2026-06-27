using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Voxa.Speech.Voxtral;

/// <summary>
/// Fully-offline streaming <see cref="ISpeechToTextEngine"/> backed by Mistral's open-weights
/// <b>Voxtral-Mini-4B-Realtime</b> served locally by vLLM over its realtime WebSocket API (VLS-009).
///
/// <para><b>One connection per utterance, buffered.</b> vLLM's <c>/v1/realtime</c> transcription is one-shot: a
/// session is <c>session.update</c> → a "ready" <c>commit</c> → <c>input_audio_buffer.append</c>… → one
/// <c>commit {final:true}</c> → streamed <c>transcription.delta</c> partials → one <c>transcription.done</c>, after
/// which the stream is finished (a bare commit never yields a <c>done</c>, and <c>final:true</c> ends the stream).
/// So a single persistent socket can't drive a conversation. Instead the engine <b>buffers each utterance</b>
/// (between VAD speech-start and speech-end), then at speech-end opens a fresh socket, replays the whole utterance,
/// sends <c>final:true</c>, and surfaces the streamed deltas as interims and the <c>done</c> as the one final —
/// then closes; the next utterance opens a new socket. Buffering (rather than streaming audio live) is what makes
/// it robust: no preroll is lost while a socket connects, and the <c>final:true</c> can never race ahead of a tail
/// append, because all sends for an utterance happen sequentially on one task.</para>
///
/// <para>Requires VAD upstream (the composer wires VAD → STT) to bracket utterances. <b>Live-verify against a real
/// vLLM build</b> — the wire field names and one-shot lifecycle are taken from the documented client example.</para>
/// </summary>
public sealed class VoxtralRealtimeSttEngine : ISpeechToTextEngine
{
    // Cap a single utterance's transcription so a wedged server can't block the caller (the STT processor awaits
    // FlushAsync on its system loop) indefinitely; the session token still cancels it sooner on teardown.
    private static readonly TimeSpan UtteranceTimeout = TimeSpan.FromSeconds(30);

    // ~0.5 s of 16 kHz mono PCM16 per append frame — small enough to stay well under any server message cap.
    private const int AppendChunkBytes = 16000;

    private readonly VoxtralOptions _options;
    private readonly IVoxtralServer _server;
    private readonly ILogger _logger;
    private readonly Channel<TranscriptionResult> _transcripts = Channel.CreateUnbounded<TranscriptionResult>();

    // The current utterance's PCM, appended on the data loop (WriteAudio) and drained on the system loop (Flush).
    private readonly object _bufferLock = new();
    private readonly MemoryStream _buffer = new();
    private bool _inUtterance;

    private Uri? _endpoint;
    private CancellationTokenSource? _sessionCts;

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
        if (_sessionCts is not null) return; // idempotent

        // Managed mode launches the vLLM server and polls it to readiness (a cold 4B load is slow); connect-only
        // just resolves the endpoint. The endpoint is reused for each per-utterance connection.
        _endpoint = await _server.StartAsync(ct).ConfigureAwait(false);
        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    }

    /// <summary>VAD speech-start: begin buffering a fresh utterance.</summary>
    public Task OnUserStartedSpeakingAsync()
    {
        lock (_bufferLock)
        {
            _buffer.SetLength(0);
            _inUtterance = true;
        }
        return Task.CompletedTask;
    }

    public ValueTask WriteAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct)
    {
        // Buffer (don't stream): the whole utterance is sent at speech-end, so no preroll is lost while a socket
        // connects and the final commit can't race a tail append.
        lock (_bufferLock)
            if (_inUtterance) _buffer.Write(pcm.Span);
        return ValueTask.CompletedTask;
    }

    public IAsyncEnumerable<TranscriptionResult> ReadTranscriptsAsync(CancellationToken ct)
        => _transcripts.Reader.ReadAllAsync(ct);

    /// <summary>VAD speech-end: transcribe the buffered utterance over a fresh one-shot connection. Awaited by the
    /// STT processor (like a batch engine), so interims stream and the one final is emitted before it returns.</summary>
    public async Task FlushAsync()
    {
        var audio = TakeUtterance();
        if (audio.Length == 0) return; // empty turn — nothing to transcribe
        await TranscribeUtteranceAsync(audio, _sessionCts?.Token ?? CancellationToken.None).ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        // An abrupt stop with no speech-end (e.g. a client disconnect mid-utterance) still transcribes the tail.
        var tail = TakeUtterance();
        if (tail.Length > 0)
        {
            try { await TranscribeUtteranceAsync(tail, _sessionCts?.Token ?? CancellationToken.None).ConfigureAwait(false); }
            catch { /* best-effort tail drain on stop */ }
        }
        _sessionCts?.Cancel();
        _transcripts.Writer.TryComplete();
    }

    private byte[] TakeUtterance()
    {
        lock (_bufferLock)
        {
            _inUtterance = false;
            if (_buffer.Length == 0) return [];
            var audio = _buffer.ToArray();
            _buffer.SetLength(0);
            return audio;
        }
    }

    private async Task TranscribeUtteranceAsync(byte[] audio, CancellationToken sessionCt)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(sessionCt);
        timeout.CancelAfter(UtteranceTimeout);
        var ct = timeout.Token;

        using var ws = new ClientWebSocket();
        _logger.LogDebug("Voxtral connecting to {Endpoint}", _endpoint);
        try
        {
            await ws.ConnectAsync(_endpoint!, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException)
        {
            // Surface a connect failure instead of silently producing no transcript: fault the stream so
            // SpeechToTextProcessor raises an ErrorFrame with remediation guidance.
            _transcripts.Writer.TryComplete(new VoxaModelUnavailableException(
                $"Could not connect to the Voxtral vLLM realtime server at {_endpoint}. In connect-only mode, ensure " +
                "your vLLM server is running; in managed mode, check the server logs (it may still be loading the model).", ex));
            return;
        }

        try
        {
            // Handshake, then the whole utterance, then the terminal commit — all sequential on this one task, so
            // there are no concurrent sends and final:true is always last on the wire.
            await SendAsync(ws, VoxtralWire.SessionUpdate(_options.Model, _options.Language, _options.DelayMs), ct).ConfigureAwait(false);
            await SendAsync(ws, VoxtralWire.Commit(final: false), ct).ConfigureAwait(false); // "ready to start"
            for (var offset = 0; offset < audio.Length; offset += AppendChunkBytes)
            {
                var len = Math.Min(AppendChunkBytes, audio.Length - offset);
                await SendAsync(ws, VoxtralWire.AppendAudio(audio.AsSpan(offset, len)), ct).ConfigureAwait(false);
            }
            await SendAsync(ws, VoxtralWire.Commit(final: true), ct).ConfigureAwait(false);

            await ReceiveUntilDoneAsync(ws, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* session torn down / utterance timed out */ }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
        {
            // Socket dropped mid-utterance — log and skip this utterance rather than fault the whole session.
            _logger.LogWarning(ex, "Voxtral utterance transcription was interrupted");
        }
        finally
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                {
                    using var close = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", close.Token).ConfigureAwait(false);
                }
            }
            catch { /* best-effort close */ }
        }
    }

    private async Task ReceiveUntilDoneAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var running = new StringBuilder();
        var buffer = new byte[16 * 1024];
        using var msg = new MemoryStream();
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            ValueWebSocketReceiveResult result;
            try { result = await ws.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false); }
            catch (WebSocketException) { break; }

            if (result.MessageType == WebSocketMessageType.Close) break;
            msg.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;

            if (result.MessageType == WebSocketMessageType.Text &&
                Ingest(Encoding.UTF8.GetString(msg.GetBuffer(), 0, (int)msg.Length), running))
                break; // a done / error frame ends this utterance's stream
            msg.SetLength(0);
        }
    }

    // Dispatch one parsed server frame. Returns true when the utterance's stream is finished (done/error).
    private bool Ingest(string json, StringBuilder running)
    {
        if (!VoxtralWire.TryParseServerMessage(json, out var m)) return false;
        switch (m.Kind)
        {
            case VoxtralServerEvent.Delta:
                if (m.Text.Length == 0) return false;
                running.Append(m.Text);
                _transcripts.Writer.TryWrite(new TranscriptionResult(running.ToString(), IsFinal: false, _options.Language));
                return false;

            case VoxtralServerEvent.Done:
                // Prefer the server's authoritative full text; fall back to the accumulated deltas if it's empty.
                var text = m.Text.Length > 0 ? m.Text : running.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                    _transcripts.Writer.TryWrite(new TranscriptionResult(text, IsFinal: true, _options.Language));
                return true; // utterance complete

            case VoxtralServerEvent.Error:
                // The server rejected the request or hit a runtime failure (e.g. a bad model name). Surface it
                // instead of waiting forever: fault the transcript stream so SpeechToTextProcessor raises an ErrorFrame.
                var error = m.Text.Length > 0 ? m.Text : "unspecified error";
                _logger.LogError("Voxtral server reported an error: {Error}", error);
                _transcripts.Writer.TryComplete(new VoxaModelUnavailableException($"Voxtral server error: {error}"));
                return true;

            default:
                return false;
        }
    }

    private static ValueTask SendAsync(ClientWebSocket ws, ReadOnlyMemory<byte> bytes, CancellationToken ct)
        => ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);

    public async ValueTask DisposeAsync()
    {
        _sessionCts?.Cancel();
        _sessionCts?.Dispose();
        _buffer.Dispose();
        _transcripts.Writer.TryComplete();
        await _server.DisposeAsync().ConfigureAwait(false); // managed mode: kill the server process tree
        GC.SuppressFinalize(this);
    }
}
