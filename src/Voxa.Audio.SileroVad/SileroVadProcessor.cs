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
    private readonly SileroVadEngine _engine;
    private readonly ILogger _logger;

    private readonly List<float> _samples = new();
    private readonly TimeSpan _windowDuration;
    private bool _isSpeaking;
    private TimeSpan _voicedAccum = TimeSpan.Zero;
    private TimeSpan _unvoicedAccum = TimeSpan.Zero;

    // Pre-roll: keeps the last N windows so when the gate opens we don't lose the first word.
    private readonly Queue<byte[]> _preroll = new();
    private readonly int _prerollWindows;

    private float _peakProbThisSecond;
    private double _peakRmsThisSecond;
    private int _windowsThisSecond;
    private readonly int _windowsPerSecond;

    public SileroVadProcessor(SileroVadOptions? options = null, ILogger? logger = null)
        : base("SileroVad")
    {
        _options = options ?? new SileroVadOptions();
        _engine = new SileroVadEngine(_options.SampleRate);
        _logger = logger ?? NullLogger.Instance;
        _windowDuration = TimeSpan.FromSeconds((double)_engine.WindowSize / _options.SampleRate);
        _windowsPerSecond = _options.SampleRate / _engine.WindowSize;
        _prerollWindows = Math.Max(1, (int)Math.Ceiling(_options.PrerollDuration.TotalMilliseconds / _windowDuration.TotalMilliseconds));
    }

    /// <summary>The configured sample rate. Audio frames at other rates are forwarded untouched (with a warning).</summary>
    public int SampleRate => _options.SampleRate;

    /// <summary>The exact window size the engine consumes per inference (512 at 16 kHz, 256 at 8 kHz).</summary>
    public int WindowSize => _engine.WindowSize;

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

        while (_samples.Count >= _engine.WindowSize)
        {
            var window = new float[_engine.WindowSize];
            _samples.CopyTo(0, window, 0, _engine.WindowSize);
            _samples.RemoveRange(0, _engine.WindowSize);

            float prob = _engine.Probability(window);
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
            if (!_isSpeaking && voiced && _voicedAccum >= _options.StartDuration)
            {
                _isSpeaking = true;
            }
            else if (_isSpeaking && !voiced && _unvoicedAccum >= _options.StopDuration)
            {
                _isSpeaking = false;
            }

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
            var pcmOut = new byte[_engine.WindowSize * 2];
            for (int i = 0; i < _engine.WindowSize; i++)
            {
                short s = (short)Math.Clamp(window[i] * 32768f, short.MinValue, short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(pcmOut.AsSpan(i * 2, 2), s);
            }

            if (_isSpeaking && !wasSpeaking)
            {
                _logger.LogDebug(
                    "SileroVad: gate OPENED (prob {Prob:F2}, rms {Rms:F4}, prerolling {Count} windows ≈ {Ms} ms)",
                    prob, rms, _preroll.Count, (int)(_preroll.Count * _windowDuration.TotalMilliseconds));
                await PushFrameAsync(new UserStartedSpeakingFrame(), ct).ConfigureAwait(false);

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

    private static double ComputeRms(ReadOnlySpan<float> window)
    {
        if (window.Length == 0) return 0;
        double sumSquares = 0;
        for (int i = 0; i < window.Length; i++) sumSquares += window[i] * window[i];
        return Math.Sqrt(sumSquares / window.Length);
    }

    protected override ValueTask OnEndAsync(EndFrame frame, CancellationToken ct)
    {
        _engine.Dispose();
        return ValueTask.CompletedTask;
    }
}
