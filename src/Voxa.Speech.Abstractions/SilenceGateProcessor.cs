using System.Buffers.Binary;
using Voxa.Frames;
using Voxa.Processors;

namespace Voxa.Speech;

/// <summary>
/// Energy-based voice activity gate. Drops <see cref="AudioRawFrame"/>s whose RMS amplitude
/// is below a threshold (with hold-open hangover so brief gaps between syllables don't clip
/// speech), and emits <see cref="UserStartedSpeakingFrame"/> / <see cref="UserStoppedSpeakingFrame"/>
/// on transitions. The downstream <see cref="SpeechToTextProcessor"/> uses the speech-end
/// signal to force-flush its batch engine immediately — so a single utterance gets
/// transcribed within ~hangover ms of the last syllable, not after the next periodic tick.
///
/// <para>
/// All non-audio frames forwarded unchanged. Insert immediately before any STT processor.
/// Skip on the Voice Live composite path — Voice Live includes its own server-side VAD.
/// </para>
/// </summary>
public sealed class SilenceGateProcessor : FrameProcessor
{
    private readonly double _rmsThreshold;
    private readonly TimeSpan _hangover;
    private DateTime _gateOpenUntilUtc = DateTime.MinValue;
    private bool _gateOpen;

    /// <summary>Construct the gate.</summary>
    /// <param name="rmsThreshold">
    /// Normalized-RMS threshold in [0..1]. Defaults to 0.005 — drops pure silence and most
    /// quiet room noise but lets quieter speech through. Raise to 0.01–0.02 for noisier
    /// environments; lower to 0.002 if quiet talkers get cut off.
    /// </param>
    /// <param name="hangover">
    /// How long the gate stays open after the most recent above-threshold frame — also the
    /// "end-of-turn" timeout for downstream STT flush. Defaults to 800 ms (matches Pipecat's
    /// <c>stop_secs=0.8</c>). Lower (300–500 ms) for snappier turn-taking with crisp speakers;
    /// raise (1000–1500 ms) for slow speakers or anyone who pauses mid-sentence to think —
    /// without that headroom, "Good morning, my name is Michael, *(pause)* what time is it?"
    /// will be transcribed as two separate turns.
    /// </param>
    /// <param name="name">Optional processor name for logging.</param>
    public SilenceGateProcessor(
        double rmsThreshold = 0.005,
        TimeSpan? hangover = null,
        string? name = null)
        : base(name ?? "SilenceGate")
    {
        if (rmsThreshold < 0) throw new ArgumentOutOfRangeException(nameof(rmsThreshold));
        _rmsThreshold = rmsThreshold;
        _hangover = hangover ?? TimeSpan.FromMilliseconds(800);
    }

    /// <summary>The threshold this gate is configured with.</summary>
    public double RmsThreshold => _rmsThreshold;

    /// <summary>The hangover this gate is configured with.</summary>
    public TimeSpan Hangover => _hangover;

    protected override async ValueTask ProcessFrameAsync(Frame frame, CancellationToken ct)
    {
        if (frame is AudioRawFrame audio)
        {
            var now = DateTime.UtcNow;
            if (ComputeRms(audio.Pcm.Span) >= _rmsThreshold)
            {
                _gateOpenUntilUtc = now + _hangover;
            }
            bool isOpenNow = now < _gateOpenUntilUtc;

            if (isOpenNow && !_gateOpen)
            {
                _gateOpen = true;
                await PushFrameAsync(new UserStartedSpeakingFrame(), ct).ConfigureAwait(false);
            }
            else if (!isOpenNow && _gateOpen)
            {
                _gateOpen = false;
                await PushFrameAsync(new UserStoppedSpeakingFrame(), ct).ConfigureAwait(false);
            }

            if (!isOpenNow)
            {
                return; // gate closed — drop the audio frame
            }
        }
        await PushFrameAsync(frame, ct).ConfigureAwait(false);
    }

    /// <summary>Compute normalized RMS of 16-bit signed little-endian PCM. Returns 0 for empty input.</summary>
    public static double ComputeRms(ReadOnlySpan<byte> pcm)
    {
        int sampleCount = pcm.Length / 2;
        if (sampleCount == 0) return 0;
        double sumSquares = 0;
        for (int i = 0; i < sampleCount; i++)
        {
            short sample = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(i * 2, 2));
            double normalized = sample / 32768.0;
            sumSquares += normalized * normalized;
        }
        return Math.Sqrt(sumSquares / sampleCount);
    }
}
