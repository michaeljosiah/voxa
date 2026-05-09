using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Voxa.Frames;
using Voxa.Processors;

namespace Voxa.Audio.SileroVad;

/// <summary>
/// ML-based voice activity gate using the Silero VAD ONNX model. Buffers incoming audio into
/// fixed-size windows, runs the model on each window, and applies hysteresis + minimum-duration
/// rules to decide when to open and close the gate.
///
/// <para>
/// Emits <see cref="UserStartedSpeakingFrame"/> / <see cref="UserStoppedSpeakingFrame"/> on
/// transitions (same shape as <c>SilenceGateProcessor</c> and Voice Live, so the downstream
/// <c>SpeechToTextProcessor</c> automatically force-flushes its batch engine on speech-end).
/// </para>
///
/// <para>
/// Use as a drop-in upgrade for <c>SilenceGateProcessor</c> when energy-only filtering isn't
/// enough — keyboard noise, fans, distant chatter all stay closed; quiet but real speech opens.
/// </para>
/// </summary>
public sealed class SileroVadProcessor : FrameProcessor
{
    private readonly SileroVadOptions _options;
    private readonly SileroVadEngine _engine;
    private readonly ILogger _logger;

    private readonly List<float> _samples = new();
    private bool _isSpeaking;
    private int _consecutiveSilent;
    private int _consecutiveSpeech;

    public SileroVadProcessor(SileroVadOptions? options = null, ILogger? logger = null)
        : base("SileroVad")
    {
        _options = options ?? new SileroVadOptions();
        _engine = new SileroVadEngine(_options.SampleRate);
        _logger = logger ?? NullLogger.Instance;
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

        // Process complete windows.
        while (_samples.Count >= _engine.WindowSize)
        {
            var window = new float[_engine.WindowSize];
            _samples.CopyTo(0, window, 0, _engine.WindowSize);
            _samples.RemoveRange(0, _engine.WindowSize);

            float prob = _engine.Probability(window);
            bool wasSpeaking = _isSpeaking;
            UpdateGateState(prob);

            if (_isSpeaking && !wasSpeaking)
            {
                await PushFrameAsync(new UserStartedSpeakingFrame(), ct).ConfigureAwait(false);
            }
            else if (!_isSpeaking && wasSpeaking)
            {
                await PushFrameAsync(new UserStoppedSpeakingFrame(), ct).ConfigureAwait(false);
            }

            if (_isSpeaking)
            {
                // Re-emit this window's worth of audio downstream (gate is open).
                var pcmOut = new byte[_engine.WindowSize * 2];
                for (int i = 0; i < _engine.WindowSize; i++)
                {
                    short s = (short)Math.Clamp(window[i] * 32768f, short.MinValue, short.MaxValue);
                    BinaryPrimitives.WriteInt16LittleEndian(pcmOut.AsSpan(i * 2, 2), s);
                }
                await PushFrameAsync(new AudioRawFrame(pcmOut, audio.SampleRate, audio.Channels), ct).ConfigureAwait(false);
            }
        }
    }

    private void UpdateGateState(float prob)
    {
        if (_isSpeaking)
        {
            if (prob < _options.DeactivationThreshold)
            {
                _consecutiveSilent++;
                _consecutiveSpeech = 0;
                if (_consecutiveSilent >= _options.MinSilenceWindows)
                {
                    _isSpeaking = false;
                    _consecutiveSilent = 0;
                }
            }
            else
            {
                _consecutiveSilent = 0;
            }
        }
        else
        {
            if (prob >= _options.ActivationThreshold)
            {
                _consecutiveSpeech++;
                _consecutiveSilent = 0;
                if (_consecutiveSpeech >= _options.MinSpeechWindows)
                {
                    _isSpeaking = true;
                    _consecutiveSpeech = 0;
                }
            }
            else
            {
                _consecutiveSpeech = 0;
            }
        }
    }

    protected override ValueTask OnEndAsync(EndFrame frame, CancellationToken ct)
    {
        _engine.Dispose();
        return ValueTask.CompletedTask;
    }
}
