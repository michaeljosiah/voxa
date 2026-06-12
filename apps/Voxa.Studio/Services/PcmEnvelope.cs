namespace Voxa.Studio.Services;

/// <summary>
/// Reduces PCM16 mono to a fixed-width amplitude envelope for the waveform strip — peak |sample|
/// per bucket, normalized to the loudest bucket so quiet takes still read as shape, not silence.
/// </summary>
internal static class PcmEnvelope
{
    public static IReadOnlyList<float> Compute(ReadOnlySpan<byte> pcm, int buckets = 56)
    {
        var samples = pcm.Length / 2;
        if (samples == 0 || buckets <= 0) return [];

        var peaks = new float[Math.Min(buckets, samples)];
        var perBucket = samples / (double)peaks.Length;
        for (int b = 0; b < peaks.Length; b++)
        {
            int start = (int)(b * perBucket);
            int end = b == peaks.Length - 1 ? samples : (int)((b + 1) * perBucket);
            int peak = 0;
            for (int i = start; i < end; i++)
            {
                int s = Math.Abs((int)BitConverter.ToInt16(pcm.Slice(i * 2, 2)));
                if (s > peak) peak = s;
            }
            peaks[b] = peak;
        }

        var max = peaks.Max();
        if (max <= 0) return peaks; // digital silence stays flat — honest
        for (int b = 0; b < peaks.Length; b++)
            peaks[b] = peaks[b] / max;
        return peaks;
    }
}
