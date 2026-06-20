using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Voxa.Frames;
using Voxa.Processors;

namespace Voxa.Speech;

/// <summary>
/// Vendor-neutral STT processor. Pipes inbound <see cref="AudioRawFrame"/>s into an
/// <see cref="ISpeechToTextEngine"/> and emits <see cref="TranscriptionFrame"/>s for downstream.
/// Pair with a vendor engine from <c>Voxa.Speech.Azure</c>, <c>Voxa.Speech.OpenAI</c>, etc.
/// </summary>
public sealed class SpeechToTextProcessor : FrameProcessor
{
    private readonly Func<ISpeechToTextEngine> _engineFactory;
    private readonly ILogger _logger;
    private ISpeechToTextEngine? _engine;
    private Task? _readLoop;

    // Eager-STT coordination (VRT-002 WS1), touched from the system loop (ProcessFrameAsync) and the read loop
    // (ReadLoopAsync) under _specLock. A speculative final is HELD (not forwarded) until the VAD confirms or
    // supersedes the utterance — forwarding it eagerly would let a fast batch engine's final start a turn that
    // a later supersede could not retract (the race that would break the suppression guarantee).
    private readonly object _specLock = new();
    private readonly HashSet<long> _superseded = new();                          // final tagged with this id ⇒ drop
    private readonly HashSet<long> _confirmed = new();                           // final tagged with this id ⇒ forward
    private readonly Dictionary<long, TranscriptionResult> _heldFinals = new();  // final arrived before its verdict
    private bool _speculativePending;
    private long _activeSpecId;

    /// <summary>Construct with an existing engine instance (one-shot use).</summary>
    public SpeechToTextProcessor(ISpeechToTextEngine engine, ILogger? logger = null)
        : this(() => engine, logger) { }

    /// <summary>Construct with a factory — engine is created on Start, disposed on End.</summary>
    public SpeechToTextProcessor(Func<ISpeechToTextEngine> engineFactory, ILogger? logger = null)
        : base("SpeechToText")
    {
        _engineFactory = engineFactory ?? throw new ArgumentNullException(nameof(engineFactory));
        _logger = logger ?? NullLogger.Instance;
    }

    protected override async ValueTask OnStartAsync(StartFrame frame, CancellationToken ct)
    {
        _engine = _engineFactory();
        await _engine.StartAsync(ct).ConfigureAwait(false);
        _readLoop = Task.Run(() => ReadLoopAsync(ct));
    }

    protected override async ValueTask OnEndAsync(EndFrame frame, CancellationToken ct)
    {
        if (_engine is not null)
        {
            try { await _engine.StopAsync().ConfigureAwait(false); } catch { /* best-effort */ }
            await _engine.DisposeAsync().ConfigureAwait(false);
            _engine = null;
        }

        if (_readLoop is not null)
        {
            try { await _readLoop.ConfigureAwait(false); } catch { }
            _readLoop = null;
        }
    }

    protected override async ValueTask ProcessFrameAsync(Frame frame, CancellationToken ct)
    {
        if (_engine is not null)
        {
            switch (frame)
            {
                case AudioRawFrame audio:
                    // Audio is consumed by STT; transcriptions come back via the read loop.
                    await _engine.WriteAudioAsync(audio.Pcm, ct).ConfigureAwait(false);
                    return;

                case SpeculativeUtteranceFrame spec:
                    // Eager STT (VRT-002 WS1): flush speculatively, or record a supersession.
                    await HandleSpeculativeAsync(spec).ConfigureAwait(false);
                    break; // also forward — the sink reads the marker (continuation, not barge-in)

                case UserStartedSpeakingFrame:
                    // A new utterance is starting; no speculative pass is pending for it yet.
                    lock (_specLock) _speculativePending = false;
                    break;

                case UserStoppedSpeakingFrame stopped:
                    // Speech-end: flush (or promote a pending speculative pass) and forward. HandleUserStoppedAsync
                    // forwards the frame (and any promoted transcript) itself, so this case returns.
                    await HandleUserStoppedAsync(stopped, ct).ConfigureAwait(false);
                    return;
            }
        }

        // Forward control + non-audio frames so Start/End/speaking events reach the sink.
        await PushFrameAsync(frame, ct).ConfigureAwait(false);
    }

    private async Task HandleSpeculativeAsync(SpeculativeUtteranceFrame spec)
    {
        if (_engine is null) return;

        if (spec.Superseded)
        {
            // Resume / smart-turn-false: this speculative utterance is dead. Drop its final whether it already
            // arrived (held) or arrives later (recorded in _superseded) — the suppression guarantee, robust to
            // the engine finishing inference before this frame lands.
            lock (_specLock)
            {
                _heldFinals.Remove(spec.UtteranceId);
                _superseded.Add(spec.UtteranceId);
                if (_activeSpecId == spec.UtteranceId) _speculativePending = false;
                PruneLocked();
            }
            return;
        }

        lock (_specLock) { _speculativePending = true; _activeSpecId = spec.UtteranceId; }
        // Speculative flush: transcribe the buffered utterance now, tagged with this id, overlapping the rest of
        // the hangover. Its result is HELD until the turn is confirmed or superseded (see ReadLoopAsync). Engines
        // that don't batch treat FlushAsync(id) as a no-op (no speculative final to hold).
        try { await _engine.FlushAsync(spec.UtteranceId).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "SpeechToTextProcessor: speculative FlushAsync threw"); }
    }

    private async Task HandleUserStoppedAsync(UserStoppedSpeakingFrame frame, CancellationToken ct)
    {
        bool promote;
        long specId;
        lock (_specLock) { promote = _speculativePending; specId = _activeSpecId; _speculativePending = false; }

        TranscriptionResult? promoted = null;
        if (promote && _engine is not null)
        {
            // Confirm ⇒ promote: the speculative pass IS this turn's transcription. Release its held final (or
            // mark the id confirmed so the read loop forwards it when it arrives), and drop the engine buffer
            // without a second transcription (no double STT pass).
            lock (_specLock)
            {
                if (!_heldFinals.Remove(specId, out promoted)) { _confirmed.Add(specId); PruneLocked(); }
            }
            try { await _engine.DiscardBufferedAudioAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "SpeechToTextProcessor: DiscardBufferedAudioAsync threw"); }
        }
        else if (_engine is not null)
        {
            // Classic batch flush at speech-end (no eager pass pending).
            try { await _engine.FlushAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "SpeechToTextProcessor: engine FlushAsync threw"); }
        }

        // Forward the speech-end signal first, then the promoted transcript — matching the classic
        // UserStopped-then-transcript ordering the sink and downstream stages expect.
        await PushFrameAsync(frame, ct).ConfigureAwait(false);
        if (promoted is not null)
            await PushFrameAsync(new TranscriptionFrame(promoted.Text, promoted.IsFinal, promoted.Language), ct).ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        if (_engine is null) return;
        try
        {
            await foreach (var t in _engine.ReadTranscriptsAsync(ct).ConfigureAwait(false))
            {
                // A tagged speculative final is never forwarded on arrival: it is dropped (superseded),
                // forwarded (already confirmed), or HELD until the VAD decides (VRT-002 WS1). Holding closes
                // the race where a fast engine returns the final before the supersede frame lands.
                if (t.IsFinal && t.UtteranceId is { } id)
                {
                    EagerDecision decision;
                    lock (_specLock)
                    {
                        if (_superseded.Remove(id)) decision = EagerDecision.Drop;
                        else if (_confirmed.Remove(id)) decision = EagerDecision.Forward;
                        else { _heldFinals[id] = t; PruneLocked(); decision = EagerDecision.Hold; }
                    }

                    if (decision == EagerDecision.Drop)
                    {
                        _logger.LogDebug("SpeechToTextProcessor: dropped superseded speculative final (utterance {Id})", id);
                        continue;
                    }
                    if (decision == EagerDecision.Hold)
                    {
                        _logger.LogDebug("SpeechToTextProcessor: holding speculative final (utterance {Id}) pending turn confirmation", id);
                        continue;
                    }
                    // Forward (already confirmed) — fall through.
                }

                await PushFrameAsync(new TranscriptionFrame(t.Text, t.IsFinal, t.Language), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SpeechToTextProcessor: STT engine read loop failed");
            await PushErrorAsync($"STT engine failed: {ex.Message}", ex, ct).ConfigureAwait(false);
        }
    }

    private enum EagerDecision { Forward, Drop, Hold }

    // Caller must hold _specLock. Safety valve only: ids whose finals never arrive (empty transcription, a
    // cancelled flush) would otherwise accumulate. In normal operation these collections hold 0–1 entries.
    private void PruneLocked()
    {
        const int cap = 256;
        if (_superseded.Count > cap) _superseded.Clear();
        if (_confirmed.Count > cap) _confirmed.Clear();
        if (_heldFinals.Count > cap) _heldFinals.Clear();
    }
}
