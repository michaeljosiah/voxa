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

    // Eager-STT coordination (VRT-002 WS1). Guards the suppression set and the "a speculative flush already
    // covers the current utterance" flag, both touched from the system loop (ProcessFrameAsync) and the read
    // loop (ReadLoopAsync). _superseded holds utterance ids whose speculative final must be dropped.
    private readonly object _specLock = new();
    private readonly HashSet<long> _superseded = new();
    private bool _speculativePending;

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

                case UserStoppedSpeakingFrame:
                    // Speech-end signal — drain whatever the batch engine has buffered immediately instead of
                    // waiting for its periodic timer. But if an un-superseded speculative flush already covers
                    // this utterance, its in-flight transcription IS the turn (VRT-002 "confirm ⇒ promote") —
                    // don't flush a second time. The frame still flows downstream either way.
                    bool promote;
                    lock (_specLock) { promote = _speculativePending; _speculativePending = false; }
                    if (promote)
                    {
                        // The speculative flush already covers this utterance — drop the engine's buffer
                        // without a second transcription (no double STT pass).
                        try { await _engine.DiscardBufferedAudioAsync().ConfigureAwait(false); }
                        catch (Exception ex) { _logger.LogWarning(ex, "SpeechToTextProcessor: DiscardBufferedAudioAsync threw"); }
                    }
                    else
                    {
                        try { await _engine.FlushAsync().ConfigureAwait(false); }
                        catch (Exception ex) { _logger.LogWarning(ex, "SpeechToTextProcessor: engine FlushAsync threw"); }
                    }
                    break;
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
            // The VAD marked this speculative utterance stale (user resumed, or ConfirmTurnEnd returned false).
            // Record the id so the read loop drops its final before it becomes a TranscriptionFrame — even if a
            // batch engine already ran the inference to completion on its lifetime token (the guarantee).
            lock (_specLock)
            {
                _superseded.Add(spec.UtteranceId);
                _speculativePending = false;
                // Bound the set: ids whose finals never arrive (flush-cancelled / streaming no-op) would
                // otherwise leak. Past a generous cap the oldest are long resolved — keep only this one.
                if (_superseded.Count > 1024) { _superseded.Clear(); _superseded.Add(spec.UtteranceId); }
            }
            return;
        }

        lock (_specLock) _speculativePending = true;
        // Speculative flush: transcribe the buffered utterance now, tagged with this id, overlapping the rest
        // of the hangover. Engines that don't batch treat FlushAsync(id) as a no-op (nothing to suppress).
        try { await _engine.FlushAsync(spec.UtteranceId).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "SpeechToTextProcessor: speculative FlushAsync threw"); }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        if (_engine is null) return;
        try
        {
            await foreach (var t in _engine.ReadTranscriptsAsync(ct).ConfigureAwait(false))
            {
                // Drop a stale speculative final BEFORE it becomes a TranscriptionFrame / starts a turn:
                // the user resumed within the eager window, or ConfirmTurnEnd returned false (VRT-002 WS1).
                if (t.IsFinal && t.UtteranceId is { } id)
                {
                    bool drop;
                    lock (_specLock) drop = _superseded.Remove(id);
                    if (drop)
                    {
                        _logger.LogDebug(
                            "SpeechToTextProcessor: dropped superseded speculative final (utterance {Id})", id);
                        continue;
                    }
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
}
