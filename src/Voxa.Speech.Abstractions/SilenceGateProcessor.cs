using System.Buffers.Binary;
using Voxa.Frames;
using Voxa.Processors;

namespace Voxa.Speech;

/// <summary>
/// Drops <see cref="AudioRawFrame"/>s whose RMS amplitude is below a threshold. Sit it between
/// an audio source and an STT engine to stop silent / near-silent windows from reaching the STT
/// — Whisper in particular is well-known for hallucinating "thank you" / "bye" / "..." on
/// near-silent audio.
///
/// <para>
/// Uses a "hangover" (hold-open) so brief amplitude dips between syllables don't clip speech:
/// once any frame crosses the threshold, the gate stays open for <see cref="Hangover"/>
/// regardless of subsequent frame amplitude.
/// </para>
///
/// <para>
/// All non-audio frames (control, system, transcriptions, etc.) are forwarded unchanged. For
/// production VAD with model-based speech/non-speech classification, use a dedicated package
/// (e.g. Silero ONNX) — this gate is a cheap energy-only filter.
/// </para>
/// </summary>
public sealed class SilenceGateProcessor : FrameProcessor
{
    private readonly double _rmsThreshold;
    private readonly TimeSpan _hangover;
    private DateTime _gateOpenUntilUtc = DateTime.MinValue;

    /// <summary>
    /// Construct the gate.
    /// </summary>
    /// <param name="rmsThreshold">
    /// Normalized-RMS threshold in [0..1]. Defaults to 0.005 — drops pure silence and most
    /// quiet room noise but lets quiet speech through. Raise to 0.01–0.02 for noisier
    /// environments; lower to 0.002 if quiet talkers are getting cut off.
    /// </param>
    /// <param name="hangover">
    /// How long to keep the gate open after the most recent above-threshold frame. Defaults
    /// to 500 ms — long enough to span a syllable gap, short enough that real silence doesn't
    /// leak through.
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
        _hangover = hangover ?? TimeSpan.FromMilliseconds(500);
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
            if (now >= _gateOpenUntilUtc)
            {
                return; // gate closed — drop the frame
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
