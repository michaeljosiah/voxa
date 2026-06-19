using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Voxa.Frames;
using Voxa.Processors;

namespace Voxa.Audio.SileroVad;

/// <summary>
/// ML-based voice activity gate combining the Silero VAD v6 ONNX model with an energy floor
/// (Pipecat-style: a window is "voiced" only if confidence ≥ threshold AND RMS ≥ floor) and
/// time-based start/stop windows so the gate doesn't flap on a single noisy or quiet frame.
///
/// <para>
/// Emits <see cref="UserStartedSpeakingFrame"/> / <see cref="UserStoppedSpeakingFrame"/> on
/// transitions — same shape as <c>SilenceGateProcessor</c> and Voice Live, so the downstream
/// <c>SpeechToTextProcessor</c> automatically force-flushes its batch engine on speech-end.
/// </para>
///
/// <para>
/// Use as a drop-in upgrade for <c>SilenceGateProcessor</c> when energy-only filtering isn't
/// enough — keyboard noise / fans / distant chatter stay closed; quiet but real speech opens.
/// </para>
/// </summary>
public sealed class SileroVadProcessor : FrameProcessor
{
    private readonly SileroVadOptions _options;
    private readonly SileroVadEngine? _engine;
    private readonly Func<float[], float>? _probabilityOverride; // test seam: bypass the ONNX model
    private readonly int _windowSize;
    private readonly ILogger _logger;

    private readonly List<float> _samples = new();
    private readonly float[] _window;   // reusable scratch for one inference window; never escapes into a frame
    private readonly TimeSpan _windowDuration;
    private bool _isSpeaking;
    private TimeSpan _voicedAccum = TimeSpan.Zero;
    private TimeSpan _unvoicedAccum = TimeSpan.Zero;

    // Eager STT (VRT-002 WS1): one speculative dispatch per hangover, tagged with a monotonic utterance id.
    private bool _eagerDispatched;
    private long _eagerUtteranceId;
    private long _eagerUtteranceCounter;

    // Force-split (VRT-002 WS2): time the gate has been continuously open for the current utterance.
    private TimeSpan _utteranceOpenDuration = TimeSpan.Zero;

    // Pre-roll: keeps the last N windows so when the gate opens we don't lose the first word.
    private readonly Queue<byte[]> _preroll = new();
    private readonly int _prerollWindows;

    // Rolling buffer of the current turn's speech (up to 8 s) passed to a smart-turn ConfirmTurnEnd callback.
    private readonly Queue<byte[]> _recentSpeech = new();
    private readonly int _recentSpeechWindows;

    private float _peakProbThisSecond;
    private double _peakRmsThisSecond;
    private int _windowsThisSecond;
    private readonly int _windowsPerSecond;

    public SileroVadProcessor(SileroVadOptions? options = null, ILogger? logger = null)
        : this(options ?? new SileroVadOptions(), probability: null, windowSizeOverride: null, logger: logger)
    { }

    /// <summary>
    /// Test seam (VRT-002): bypasses the ONNX model. <paramref name="probability"/> receives each inference
    /// window and returns the speech probability, so the eager / turn state machine can be driven with a
    /// deterministic, synthetic VAD-probability sequence without loading or running the real model.
    /// </summary>
    internal SileroVadProcessor(
        SileroVadOptions options, int windowSize, Func<float[], float> probability, ILogger? logger = null)
        : this(options, probability ?? throw new ArgumentNullException(nameof(probability)), windowSize, logger)
    { }

    private SileroVadProcessor(
        SileroVadOptions options, Func<float[], float>? probability, int? windowSizeOverride, ILogger? logger)
        : base("SileroVad")
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _probabilityOverride = probability;
        _engine = probability is null ? new SileroVadEngine(_options.SampleRate) : null;
        _windowSize = windowSizeOverride ?? _engine!.WindowSize;
        _window = new float[_windowSize];
        _logger = logger ?? NullLogger.Instance;
        _windowDuration = TimeSpan.FromSeconds((double)_windowSize / _options.SampleRate);
        _windowsPerSecond = Math.Max(1, _options.SampleRate / _windowSize);
        _prerollWindows = Math.Max(1, (int)Math.Ceiling(_options.PrerollDuration.TotalMilliseconds / _windowDuration.TotalMilliseconds));
        // Up to 8 s of the current turn for the smart-turn seam: smart-turn-v3 wants the whole current
        // turn (bounded to its 8 s window), not just a trailing tail, or completion decisions lose context.
        _recentSpeechWindows = Math.Max(1, (int)Math.Ceiling(8000.0 / _windowDuration.TotalMilliseconds));
    }

    /// <summary>The configured sample rate. Audio frames at other rates are forwarded untouched (with a warning).</summary>
    public int SampleRate => _options.SampleRate;

    /// <summary>The exact window size the engine consumes per inference (512 at 16 kHz, 256 at 8 kHz).</summary>
    public int WindowSize => _windowSize;

    protected override async ValueTask ProcessFrameAsync(Frame frame, CancellationToken ct)
    {
        if (frame is not AudioRawFrame audio)
        {
            await PushFrameAsync(frame, ct).ConfigureAwait(false);
            return;
        }

        if (audio.SampleRate != _options.SampleRate)
        {
            _logger.LogWarning(
                "SileroVadProcessor received audio at {ActualRate} Hz but is configured for {ExpectedRate} Hz; forwarding without VAD.",
                audio.SampleRate, _options.SampleRate);
            await PushFrameAsync(frame, ct).ConfigureAwait(false);
            return;
        }

        // Convert PCM (int16 LE) to normalized float and accumulate.
        var pcm = audio.Pcm.Span;
        int sampleCount = pcm.Length / 2;
        for (int i = 0; i < sampleCount; i++)
        {
            short s = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(i * 2, 2));
            _samples.Add(s / 32768f);
        }

        while (_samples.Count >= _windowSize)
        {
            // Reused scratch buffer — this window feeds inference + RMS only and never escapes
            // into a downstream frame, so it's safe to overwrite each iteration.
            var window = _window;
            _samples.CopyTo(0, window, 0, _windowSize);
            _samples.RemoveRange(0, _windowSize);

            float prob = _probabilityOverride?.Invoke(window) ?? _engine!.Probability(window);
            double rms = ComputeRms(window);

            // Pipecat-style: BOTH must agree.
            bool voiced = prob >= _options.ConfidenceThreshold && rms >= _options.MinRms;

            // Track sustained durations.
            if (voiced)
            {
                _voicedAccum += _windowDuration;
                _unvoicedAccum = TimeSpan.Zero;
            }
            else
            {
                _unvoicedAccum += _windowDuration;
                _voicedAccum = TimeSpan.Zero;
            }

            bool wasSpeaking = _isSpeaking;

            // Resume ⇒ discard (VRT-002 WS1): a voiced window while an eager pass is pending means the user
            // resumed within the window. Supersede the speculative utterance (the STT processor drops its
            // final) and raise NO barge-in — the marker said "continuation, not interruption". Gate stays open.
            if (voiced && _eagerDispatched)
            {
                _eagerDispatched = false;
                await PushFrameAsync(new SpeculativeUtteranceFrame(_eagerUtteranceId, Superseded: true), ct).ConfigureAwait(false);
            }

            if (!_isSpeaking && voiced && _voicedAccum >= _options.StartDuration)
            {
                _isSpeaking = true;
            }
            else if (_isSpeaking && !voiced && _unvoicedAccum >= _options.StopDuration)
            {
                // Silence timeout reached. If a smart-turn confirmer is wired, let it decide whether
                // the turn is really over; otherwise close the gate (classic behavior).
                bool confirmed = true;
                if (_options.ConfirmTurnEnd is { } confirm)
                {
                    confirmed = await confirm(SnapshotRecentSpeech(), ct).ConfigureAwait(false);
                }

                if (confirmed)
                {
                    // Confirm ⇒ promote (VRT-002 WS1): a pending eager pass IS this turn's transcription.
                    // Clear the flag WITHOUT superseding so the STT processor promotes its result (and skips
                    // the redundant flush on the UserStoppedSpeakingFrame below).
                    _isSpeaking = false;
                    _eagerDispatched = false;
                }
                else
                {
                    // Smart-turn override (VRT-002 WS1): smart-turn false > eager. Supersede any pending eager
                    // pass, then keep the gate open and wait another StopDuration of silence before re-asking.
                    if (_eagerDispatched)
                    {
                        _eagerDispatched = false;
                        await PushFrameAsync(new SpeculativeUtteranceFrame(_eagerUtteranceId, Superseded: true), ct).ConfigureAwait(false);
                    }
                    _unvoicedAccum = TimeSpan.Zero;
                }
            }
            else if (_isSpeaking && !voiced
                     && _options.EagerSttDelay is { } eager
                     && eager < _options.StopDuration
                     && !_eagerDispatched
                     && _unvoicedAccum >= eager)
            {
                // Arm (VRT-002 WS1): unvoiced silence has reached EagerSttDelay but not yet StopDuration. Emit
                // a marked speculative end-of-utterance so STT flushes the buffered utterance now, overlapping
                // the rest of the hangover. The gate stays OPEN (_isSpeaking unchanged).
                _eagerDispatched = true;
                _eagerUtteranceId = ++_eagerUtteranceCounter;
                await PushFrameAsync(new SpeculativeUtteranceFrame(_eagerUtteranceId), ct).ConfigureAwait(false);
            }

            // Live per-window observer (diagnostics hub → Studio's VAD trace). Synchronous and
            // hot-path: the composer-wired callback is a guarded TryWrite, never a block.
            _options.ProbabilityObserver?.Invoke(prob, rms, voiced, _isSpeaking);

            // Per-second diagnostic so users can tune.
            _peakProbThisSecond = Math.Max(_peakProbThisSecond, prob);
            _peakRmsThisSecond = Math.Max(_peakRmsThisSecond, rms);
            _windowsThisSecond++;
            if (_windowsThisSecond >= _windowsPerSecond)
            {
                _logger.LogDebug(
                    "SileroVad: peak prob {Prob:F2} (threshold {Conf:F2}), peak RMS {Rms:F4} (floor {Floor:F4}), gate {State}",
                    _peakProbThisSecond, _options.ConfidenceThreshold,
                    _peakRmsThisSecond, _options.MinRms,
                    _isSpeaking ? "OPEN" : "closed");
                _peakProbThisSecond = 0;
                _peakRmsThisSecond = 0;
                _windowsThisSecond = 0;
            }

            // Convert this window to PCM16 once — we'll either buffer it (gate closed) or push it (gate open).
            var pcmOut = new byte[_windowSize * 2];
            for (int i = 0; i < _windowSize; i++)
            {
                short s = (short)Math.Clamp(window[i] * 32768f, short.MinValue, short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(pcmOut.AsSpan(i * 2, 2), s);
            }

            if (_isSpeaking && !wasSpeaking)
            {
                // Fresh utterance: reset the force-split timer and any stale eager-dispatch flag.
                _utteranceOpenDuration = TimeSpan.Zero;
                _eagerDispatched = false;

                _logger.LogDebug(
                    "SileroVad: gate OPENED (prob {Prob:F2}, rms {Rms:F4}, prerolling {Count} windows ≈ {Ms} ms)",
                    prob, rms, _preroll.Count, (int)(_preroll.Count * _windowDuration.TotalMilliseconds));
                await PushFrameAsync(new UserStartedSpeakingFrame(), ct).ConfigureAwait(false);

                // Start a fresh recent-speech tail for this utterance (smart-turn seam).
                _recentSpeech.Clear();

                // Flush the pre-roll buffer so the first syllable of the utterance reaches STT.
                while (_preroll.Count > 0)
                {
                    var preBytes = _preroll.Dequeue();
                    await PushFrameAsync(new AudioRawFrame(preBytes, audio.SampleRate, audio.Channels), ct).ConfigureAwait(false);
                }
            }
            else if (!_isSpeaking && wasSpeaking)
            {
                _logger.LogDebug("SileroVad: gate CLOSED (prob {Prob:F2}, rms {Rms:F4})", prob, rms);
                await PushFrameAsync(new UserStoppedSpeakingFrame(), ct).ConfigureAwait(false);
            }

            if (_isSpeaking)
            {
                _utteranceOpenDuration += _windowDuration;

                // Force-split (VRT-002 WS2): cap continuous open-gate time. Emit a stop (so STT flushes an
                // intermediate transcription) then immediately re-open a fresh utterance so capture continues —
                // this bounds memory for a non-pausing speaker / stuck-open mic and yields periodic transcripts.
                if (_options.MaxUtteranceDuration is { } maxUtt && _utteranceOpenDuration >= maxUtt)
                {
                    if (_eagerDispatched)
                    {
                        _eagerDispatched = false;
                        await PushFrameAsync(new SpeculativeUtteranceFrame(_eagerUtteranceId, Superseded: true), ct).ConfigureAwait(false);
                    }
                    _logger.LogDebug(
                        "SileroVad: force-split at {Ms} ms (MaxUtteranceDuration) — flushing intermediate transcript",
                        (int)_utteranceOpenDuration.TotalMilliseconds);
                    await PushFrameAsync(new UserStoppedSpeakingFrame(), ct).ConfigureAwait(false);
                    await PushFrameAsync(new UserStartedSpeakingFrame(), ct).ConfigureAwait(false);
                    _utteranceOpenDuration = TimeSpan.Zero;
                    _recentSpeech.Clear();
                }

                // Keep a rolling tail of recent speech for the smart-turn confirmer (shares the
                // pcmOut reference — no extra allocation; the array is immutable once built).
                if (_options.ConfirmTurnEnd is not null)
                {
                    _recentSpeech.Enqueue(pcmOut);
                    while (_recentSpeech.Count > _recentSpeechWindows) _recentSpeech.Dequeue();
                }
                await PushFrameAsync(new AudioRawFrame(pcmOut, audio.SampleRate, audio.Channels), ct).ConfigureAwait(false);
            }
            else
            {
                // Gate closed — keep the last N windows so we can replay them when it opens.
                _preroll.Enqueue(pcmOut);
                while (_preroll.Count > _prerollWindows) _preroll.Dequeue();
            }
        }
    }

    /// <summary>Concatenate the rolling recent-speech tail into one PCM buffer for ConfirmTurnEnd.</summary>
    private ReadOnlyMemory<byte> SnapshotRecentSpeech()
    {
        if (_recentSpeech.Count == 0) return ReadOnlyMemory<byte>.Empty;
        int total = 0;
        foreach (var w in _recentSpeech) total += w.Length;
        var buf = new byte[total];
        int offset = 0;
        foreach (var w in _recentSpeech)
        {
            w.CopyTo(buf.AsSpan(offset));
            offset += w.Length;
        }
        return buf;
    }

    private static double ComputeRms(ReadOnlySpan<float> window)
    {
        if (window.Length == 0) return 0;
        double sumSquares = 0;
        for (int i = 0; i < window.Length; i++) sumSquares += window[i] * window[i];
        return Math.Sqrt(sumSquares / window.Length);
    }

    protected override ValueTask OnEndAsync(EndFrame frame, CancellationToken ct)
    {
        _engine?.Dispose();
        return ValueTask.CompletedTask;
    }
}
