using System.Buffers.Binary;
using Voxa.Frames;
using Voxa.Processors;

namespace Voxa.Speech;

/// <summary>
/// Drops <see cref="AudioRawFrame"/>s whose RMS amplitude is below a threshold. Sit it
/// between an audio source and an STT engine to stop silent / near-silent windows from
/// reaching the STT — Whisper in particular is well-known for hallucinating "thank you" /
/// "bye" / "..." on near-silent audio.
///
/// <para>
/// All non-audio frames (control, system, transcriptions, etc.) are forwarded unchanged.
/// For real production VAD with hysteresis and speech/non-speech classification, use a
/// dedicated VAD model (e.g. Silero) — this gate is a cheap energy-only filter.
/// </para>
/// </summary>
public sealed class SilenceGateProcessor : FrameProcessor
{
    private readonly double _rmsThreshold;

    /// <summary>
    /// Construct with a normalized-RMS threshold (in [0..1]). Typical values:
    /// 0.005 = quiet room, 0.01 = drops most pure silence (default), 0.02 = also drops quiet whispers.
    /// </summary>
    public SilenceGateProcessor(double rmsThreshold = 0.01, string? name = null)
        : base(name ?? "SilenceGate")
    {
        if (rmsThreshold < 0) throw new ArgumentOutOfRangeException(nameof(rmsThreshold));
        _rmsThreshold = rmsThreshold;
    }

    /// <summary>The threshold this gate is configured with.</summary>
    public double RmsThreshold => _rmsThreshold;

    protected override async ValueTask ProcessFrameAsync(Frame frame, CancellationToken ct)
    {
        if (frame is AudioRawFrame audio)
        {
            if (ComputeRms(audio.Pcm.Span) < _rmsThreshold)
            {
                return; // drop the silent frame
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
