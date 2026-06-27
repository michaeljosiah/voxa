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

    // Set on the data-loop thread by OnUserStartedSpeakingAsync, consumed on the receive loop at the next Ingest:
    // a new utterance must start from a clean interim even if the previous utterance's `done` never arrived
    // (socket hiccup / server restart), so stale text can't bleed across utterances. A volatile flag keeps
    // _running single-threaded (only the receive loop ever mutates it) without a lock.
    private volatile bool _resetRunning;

    // ClientWebSocket forbids concurrent sends, but audio appends arrive on the DATA loop while the commit at
    // speech-end (UserStoppedSpeakingFrame is a SystemFrame) arrives on the SYSTEM loop — so an append and a
    // commit can overlap. Serialize every socket write through this mutex; otherwise the overlapping send throws
    // InvalidOperationException, the catch swallows it, and the dropped commit means the utterance never finalizes.
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    // Stop-drain bookkeeping. _pendingAudio: audio appended since the last commit (uncommitted tail — set on the
    // data loop, cleared on the system loop). _awaitingDone: a commit was sent for buffered audio and its
    // transcription.done hasn't arrived yet. StopAsync waits for either before closing so the last utterance —
    // whether already committed (a clean stop right after flush) or only buffered (an abrupt disconnect) — still
    // reaches downstream. The receive loop completes _drainSignal when the done arrives.
    private volatile bool _pendingAudio;
    private volatile bool _awaitingDone;
    private volatile TaskCompletionSource? _drainSignal;

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

        // Handshake: model + the configured session knobs (language hint, realtime delay), before any audio flows.
        await SendSerializedAsync(ws, VoxtralWire.SessionUpdate(_options.Model, _options.Language, _options.DelayMs), ct)
            .ConfigureAwait(false);
        // vLLM realtime: a non-final commit right after session.update signals "ready to start" the stream.
        await SendSerializedAsync(ws, VoxtralWire.Commit(final: false), ct).ConfigureAwait(false);
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
            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await ws.SendAsync(append, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
                // Mark INSIDE the lock so a commit that next acquires the lock observes this audio. Setting it
                // after release would let a racing FlushAsync snapshot a stale "no audio" and skip _awaitingDone.
                _pendingAudio = true;
            }
            finally { _sendLock.Release(); }
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException
                                      or OperationCanceledException or InvalidOperationException)
        {
            // Socket dropped / closing / disposed — drop this chunk rather than fault the pipeline.
        }
    }

    // Serialize all socket writes (handshake / close) — ClientWebSocket allows only one outstanding send.
    // Append and commit manage the buffer flags inside the lock themselves (see WriteAudioAsync / FlushAsync).
    private async Task SendSerializedAsync(ClientWebSocket ws, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false); }
        finally { _sendLock.Release(); }
    }

    public IAsyncEnumerable<TranscriptionResult> ReadTranscriptsAsync(CancellationToken ct)
        => _transcripts.Reader.ReadAllAsync(ct);

    /// <summary>VAD speech-end: send a <b>non-final</b> commit so the server finalizes this utterance and emits
    /// <c>transcription.done</c> WITHOUT ending the stream (a <c>{"final":true}</c> commit would tell vLLM all audio
    /// is sent, stopping transcription for the rest of the session). The final flows from the receive loop when
    /// <c>done</c> arrives — never emitted locally — so there is exactly one final per utterance with no post-speech
    /// round-trip the pipeline must wait on.</summary>
    public async Task FlushAsync()
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open) return;
        try
        {
            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                // Snapshot INSIDE the lock: every append serialized before this commit has run (and set
                // _pendingAudio), and any later append is ordered after it on the wire — so _pendingAudio here is
                // exactly the audio this commit finalizes. Snapshotting before the lock could miss a racing append.
                var hadAudio = _pendingAudio;
                await ws.SendAsync(VoxtralWire.Commit(final: false), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None)
                    .ConfigureAwait(false);
                _pendingAudio = false;                 // this utterance has been committed for finalization
                if (hadAudio) _awaitingDone = true;    // the server now owes us its transcription.done
            }
            finally { _sendLock.Release(); }
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

                // Arm a drain signal so we can wait for an outstanding transcription.done before closing — either a
                // committed utterance hasn't finalized yet (_awaitingDone, e.g. a stop right after a normal flush
                // whose done is still in flight) or there's uncommitted tail audio the final commit below will
                // finalize (_pendingAudio, e.g. an abrupt disconnect mid-utterance). The receive loop completes it.
                var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _drainSignal = drain;
                if (!_awaitingDone && !_pendingAudio) drain.TrySetResult(); // nothing outstanding — don't block

                // Signal end-of-all-audio so the server flushes any tail and tears down cleanly.
                await SendSerializedAsync(ws, VoxtralWire.Commit(final: true), close.Token).ConfigureAwait(false);

                // Drain the final transcript before closing, so the last utterance still reaches downstream
                // (mirrors the other engines' stop-drain). Bounded by the close budget; the receive loop is still
                // running here (we cancel it only after), so the done is read and queued before we complete below.
                try { await drain.Task.WaitAsync(close.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* no tail arrived within the budget */ }

                await _sendLock.WaitAsync(close.Token).ConfigureAwait(false);
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "stop", close.Token).ConfigureAwait(false); }
                finally { _sendLock.Release(); }
            }
            catch { /* best-effort graceful close */ }
        }
        _cts?.Cancel();
        if (_receiveLoop is not null)
        {
            try { await _receiveLoop.ConfigureAwait(false); } catch { /* shutdown */ }
        }
        _ws = null; // release the closed socket so the engine doesn't hold a live-looking ref after stop
        _transcripts.Writer.TryComplete();
    }

    /// <summary>New utterance (VAD speech-start): drop any leftover interim from a previous utterance whose
    /// <c>done</c> never arrived, so its text can't prefix the new one. Honored on the receive loop.</summary>
    public Task OnUserStartedSpeakingAsync()
    {
        _resetRunning = true;
        return Task.CompletedTask;
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
        // A new utterance began since the last frame — clear the stale interim before appending this one's text.
        if (_resetRunning) { _resetRunning = false; _running.Clear(); }

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
                _awaitingDone = false;
                _drainSignal?.TrySetResult(); // let a stop-drain proceed now the tail final has been queued
                break;

            case VoxtralServerEvent.Error:
                // The server rejected the session/request or hit a runtime failure (e.g. a bad model name). Surface
                // it instead of waiting forever: fault the transcript stream so SpeechToTextProcessor raises an
                // ErrorFrame, and release any stop-drain. Further frames are ignored (the channel is completed).
                var error = m.Text.Length > 0 ? m.Text : "unspecified error";
                _logger.LogError("Voxtral server reported an error: {Error}", error);
                _awaitingDone = false;
                _drainSignal?.TrySetResult();
                _transcripts.Writer.TryComplete(new VoxaModelUnavailableException($"Voxtral server error: {error}"));
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
        _ws = null;
        _cts?.Dispose();
        _sendLock.Dispose();
        _transcripts.Writer.TryComplete();
        await _server.DisposeAsync().ConfigureAwait(false); // managed mode: kill the server process tree
        GC.SuppressFinalize(this);
    }
}
