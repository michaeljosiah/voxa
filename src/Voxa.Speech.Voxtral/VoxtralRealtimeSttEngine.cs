using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Voxa.Speech.Voxtral;

/// <summary>
/// Fully-offline streaming <see cref="ISpeechToTextEngine"/> backed by Mistral's open-weights
/// <b>Voxtral-Mini-4B-Realtime</b> served locally by vLLM over its realtime WebSocket API (VLS-009).
///
/// <para><b>One connection per utterance.</b> vLLM's <c>/v1/realtime</c> transcription is one-shot: a session is
/// <c>session.update</c> → a non-final <c>commit</c> (ready) → <c>input_audio_buffer.append</c>… → one
/// <c>commit {final:true}</c> → streamed <c>transcription.delta</c> partials → one <c>transcription.done</c>, after
/// which the stream is finished. A bare commit never yields a <c>done</c>, and <c>final:true</c> ends the stream —
/// so a single persistent socket cannot drive a multi-turn conversation. Instead the engine opens a fresh socket at
/// VAD speech-start (<see cref="OnUserStartedSpeakingAsync"/>), streams that utterance's audio, sends
/// <c>final:true</c> at speech-end (<see cref="FlushAsync()"/>), surfaces the deltas as interims and the
/// <c>done</c> as the one final, then closes — and reconnects for the next utterance. Each connection is exactly
/// one utterance / one final, so there is no cross-utterance state to reset.</para>
///
/// <para>Requires VAD upstream (the composer wires VAD → STT): without <c>UserStartedSpeaking</c>/<c>Stopped</c>
/// brackets there is no per-utterance connection to open or commit. <b>Live-verify against a real vLLM build</b> —
/// the wire field names and one-shot lifecycle are taken from the documented client example.</para>
/// </summary>
public sealed class VoxtralRealtimeSttEngine : ISpeechToTextEngine
{
    private readonly VoxtralOptions _options;
    private readonly IVoxtralServer _server;
    private readonly ILogger _logger;
    private readonly Channel<TranscriptionResult> _transcripts = Channel.CreateUnbounded<TranscriptionResult>();

    // ClientWebSocket forbids concurrent sends, but audio appends arrive on the DATA loop while the speech-end
    // commit (UserStoppedSpeakingFrame is a SystemFrame) arrives on the SYSTEM loop — so they can overlap on the
    // current utterance's socket. Serialize every socket write through this mutex.
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private Uri? _endpoint;
    private CancellationTokenSource? _sessionCts;

    // The current utterance's connection and its receive loop. Started on the system loop (OnUserStartedSpeaking),
    // appended-to on the data loop (WriteAudio), finalized on the system loop (FlushAsync). _ws is published only
    // after the handshake so WriteAudio never targets a half-open socket; access it via Volatile/Interlocked.
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _utteranceCts;
    private Task? _utteranceLoop;
    private TaskCompletionSource? _utteranceDone;
    private volatile bool _finalCommitted;

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

    /// <summary>VAD speech-start: finish any prior utterance, then open a fresh connection and handshake for this one.</summary>
    public async Task OnUserStartedSpeakingAsync()
    {
        if (_sessionCts is null) return;
        await EndCurrentUtteranceAsync(flushTail: true).ConfigureAwait(false);
        await OpenUtteranceAsync(_sessionCts.Token).ConfigureAwait(false);
    }

    public async ValueTask WriteAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct)
    {
        var ws = Volatile.Read(ref _ws);
        if (ws is null || ws.State != WebSocketState.Open || pcm.IsEmpty) return;

        var append = VoxtralWire.AppendAudio(pcm.Span); // base64-encode synchronously before the await
        try
        {
            await SendOnAsync(ws, append, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException
                                      or OperationCanceledException or InvalidOperationException)
        {
            // Socket dropped / closing / disposed — drop this chunk rather than fault the pipeline.
        }
    }

    public IAsyncEnumerable<TranscriptionResult> ReadTranscriptsAsync(CancellationToken ct)
        => _transcripts.Reader.ReadAllAsync(ct);

    /// <summary>VAD speech-end: send the <c>final:true</c> commit so the server finalizes the utterance and emits
    /// <c>transcription.done</c>. The final flows from the receive loop when <c>done</c> arrives (never emitted
    /// locally), and the connection is closed once it does — the next utterance opens a fresh one.</summary>
    public async Task FlushAsync()
    {
        var ws = Volatile.Read(ref _ws);
        if (ws is null || ws.State != WebSocketState.Open) return;
        try
        {
            await SendOnAsync(ws, VoxtralWire.Commit(final: true), CancellationToken.None).ConfigureAwait(false);
            _finalCommitted = true;
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException
                                      or OperationCanceledException or InvalidOperationException)
        {
            // Socket gone — nothing left to commit.
        }
    }

    public async Task StopAsync()
    {
        await EndCurrentUtteranceAsync(flushTail: true).ConfigureAwait(false);
        _sessionCts?.Cancel();
        _transcripts.Writer.TryComplete();
    }

    private async Task OpenUtteranceAsync(CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        _logger.LogDebug("Voxtral connecting to {Endpoint}", _endpoint);
        try
        {
            await ws.ConnectAsync(_endpoint!, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException)
        {
            ws.Dispose();
            // Surface a connect failure instead of silently producing no transcripts: fault the stream so
            // SpeechToTextProcessor raises an ErrorFrame with remediation guidance.
            _transcripts.Writer.TryComplete(new VoxaModelUnavailableException(
                $"Could not connect to the Voxtral vLLM realtime server at {_endpoint}. In connect-only mode, ensure " +
                "your vLLM server is running; in managed mode, check the server logs (it may still be loading the model).", ex));
            return;
        }

        // Handshake before any audio flows. Sent on the local `ws` before it is published, so no concurrent send.
        await SendOnAsync(ws, VoxtralWire.SessionUpdate(_options.Model, _options.Language, _options.DelayMs), ct).ConfigureAwait(false);
        await SendOnAsync(ws, VoxtralWire.Commit(final: false), ct).ConfigureAwait(false); // "ready to start"

        var uCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _utteranceCts = uCts;
        _utteranceDone = done;
        _finalCommitted = false;
        Volatile.Write(ref _ws, ws); // publish — WriteAudio can now append
        _utteranceLoop = Task.Run(() => ReceiveLoopAsync(ws, done, uCts.Token));
    }

    // Finish the in-flight utterance (if any): optionally flush a tail that never got a speech-end commit, drain
    // its transcription.done (bounded), then stop and dispose its receive loop. Runs on the system loop only.
    private async Task EndCurrentUtteranceAsync(bool flushTail)
    {
        var ws = Volatile.Read(ref _ws);
        var loop = _utteranceLoop;
        var done = _utteranceDone;
        var uCts = _utteranceCts;
        if (ws is null && loop is null) return;

        // Abrupt end mid-utterance (no UserStoppedSpeaking, e.g. a client disconnect): flush the tail so the
        // server still finalizes it. A clean end already sent final:true in FlushAsync.
        if (flushTail && ws is not null && ws.State == WebSocketState.Open && !_finalCommitted)
        {
            try { await SendOnAsync(ws, VoxtralWire.Commit(final: true), CancellationToken.None).ConfigureAwait(false); _finalCommitted = true; }
            catch { /* socket gone */ }
        }

        // Drain the final before tearing down, so the utterance reaches downstream (bounded so a missing done
        // can't hang teardown). Only wait when a final was actually committed — otherwise there's no done coming.
        if (done is not null && _finalCommitted)
        {
            try { await done.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException) { /* no done within the budget */ }
        }

        uCts?.Cancel(); // force the receive loop to end if the done never arrived
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); } catch { /* shutdown */ }
        }
        uCts?.Dispose();
        _utteranceLoop = null;
        _utteranceDone = null;
        _utteranceCts = null;
        _finalCommitted = false;
        Volatile.Write(ref _ws, null);
    }

    // Serialize all socket writes — ClientWebSocket allows only one outstanding send (a receive may run concurrently).
    private async Task SendOnAsync(ClientWebSocket ws, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false); }
        finally { _sendLock.Release(); }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, TaskCompletionSource done, CancellationToken ct)
    {
        var running = new StringBuilder(); // this utterance's interim, local to the loop — no cross-utterance bleed
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

                if (result.MessageType == WebSocketMessageType.Text &&
                    Ingest(Encoding.UTF8.GetString(msg.GetBuffer(), 0, (int)msg.Length), running))
                    break; // a done / error frame ends this utterance's stream
                msg.SetLength(0);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        finally
        {
            Interlocked.CompareExchange(ref _ws, null, ws); // unpublish if still current
            done.TrySetResult();
            try
            {
                if (ws.State == WebSocketState.Open)
                {
                    using var close = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", close.Token).ConfigureAwait(false);
                }
            }
            catch { /* best-effort close */ }
            ws.Dispose();
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

    public async ValueTask DisposeAsync()
    {
        _sessionCts?.Cancel();
        _utteranceCts?.Cancel();
        if (_utteranceLoop is not null)
        {
            try { await _utteranceLoop.ConfigureAwait(false); } catch { /* shutdown */ }
        }
        Volatile.Read(ref _ws)?.Dispose();
        Volatile.Write(ref _ws, null);
        _utteranceCts?.Dispose();
        _sessionCts?.Dispose();
        _sendLock.Dispose();
        _transcripts.Writer.TryComplete();
        await _server.DisposeAsync().ConfigureAwait(false); // managed mode: kill the server process tree
        GC.SuppressFinalize(this);
    }
}
