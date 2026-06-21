using Voxa.Audio;

namespace Voxa.Audio.Abstractions.Tests;

/// <summary>
/// The shared linear resampler (promoted from Studio in VTL-001) preserves signal energy within
/// tolerance and is continuous across chunk boundaries (no clicks) — for both the device bridge
/// (48 kHz ↔ 16 kHz) and the telephony edge (8 kHz ↔ 16 kHz).
/// </summary>
public class LinearResamplerTests
{
    private static short[] Sine(int rate, double hz, double seconds, double amplitude = 0.5)
    {
        var samples = new short[(int)(rate * seconds)];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (short)(Math.Sin(2 * Math.PI * hz * i / rate) * amplitude * short.MaxValue);
        return samples;
    }

    private static double Rms(ReadOnlySpan<short> samples)
    {
        double sum = 0;
        foreach (var s in samples) sum += (double)s * s;
        return Math.Sqrt(sum / samples.Length) / short.MaxValue;
    }

    [Fact]
    public void Downsample_48k_To_16k_Preserves_Tone_Energy()
    {
        var resampler = new LinearResampler(48000, 16000);
        var input = Sine(48000, 440, seconds: 0.5);
        var output = new short[resampler.MaxOutputSamples(input.Length)];
        int written = resampler.Process(input, output);

        // 3:1 ratio — one output per three inputs (±1 for boundary phase).
        Assert.InRange(written, input.Length / 3 - 1, input.Length / 3 + 1);
        // A 440 Hz tone is far below either Nyquist: energy survives linear interpolation.
        Assert.InRange(Rms(output.AsSpan(0, written)), Rms(input) * 0.95, Rms(input) * 1.05);
    }

    [Fact]
    public void Upsample_16k_To_48k_Preserves_Tone_Energy()
    {
        var resampler = new LinearResampler(16000, 48000);
        var input = Sine(16000, 440, seconds: 0.5);
        var output = new short[resampler.MaxOutputSamples(input.Length)];
        int written = resampler.Process(input, output);

        Assert.InRange(written, input.Length * 3 - 3, input.Length * 3 + 3);
        Assert.InRange(Rms(output.AsSpan(0, written)), Rms(input) * 0.95, Rms(input) * 1.05);
    }

    [Fact]
    public void Telephony_8k_To_16k_RoundTrip_Bounded_Error()
    {
        // VTL-001 inbound (8 k → 16 k) then outbound (16 k → 8 k): a 300 Hz tone (in the PSTN band)
        // survives the round trip with bounded energy loss — the resample bridge is faithful.
        var up = new LinearResampler(8000, 16000);
        var down = new LinearResampler(16000, 8000);
        var input = Sine(8000, 300, seconds: 0.5);

        var upBuf = new short[up.MaxOutputSamples(input.Length)];
        int upN = up.Process(input, upBuf);
        var downBuf = new short[down.MaxOutputSamples(upN)];
        int downN = down.Process(upBuf.AsSpan(0, upN), downBuf);

        Assert.InRange(downN, input.Length - 2, input.Length + 2);
        Assert.InRange(Rms(downBuf.AsSpan(0, downN)), Rms(input) * 0.9, Rms(input) * 1.1);
    }

    [Fact]
    public void Chunked_Processing_Equals_Whole_Buffer_Processing()
    {
        // WASAPI delivers ~20 ms chunks; the stateful resampler must produce the same stream as
        // a single pass — a boundary discontinuity would be an audible click every 20 ms.
        var whole = new LinearResampler(44100, 16000);
        var chunked = new LinearResampler(44100, 16000);
        var input = Sine(44100, 333, seconds: 0.25);

        var wholeOut = new short[whole.MaxOutputSamples(input.Length)];
        int wholeWritten = whole.Process(input, wholeOut);

        var chunkedOut = new List<short>();
        const int chunk = 882; // 20 ms at 44.1 k
        for (int offset = 0; offset < input.Length; offset += chunk)
        {
            var slice = input.AsSpan(offset, Math.Min(chunk, input.Length - offset));
            var buffer = new short[chunked.MaxOutputSamples(slice.Length)];
            int n = chunked.Process(slice, buffer);
            chunkedOut.AddRange(buffer.Take(n));
        }

        Assert.Equal(wholeWritten, chunkedOut.Count);
        for (int i = 0; i < wholeWritten; i++)
            Assert.True(Math.Abs(wholeOut[i] - chunkedOut[i]) <= 1,
                $"sample {i}: whole={wholeOut[i]} chunked={chunkedOut[i]}");
    }

    [Fact]
    public void Passthrough_Copies_Verbatim()
    {
        var resampler = new LinearResampler(16000, 16000);
        Assert.True(resampler.IsPassthrough);
        var input = Sine(16000, 200, 0.1);
        var output = new short[input.Length];
        Assert.Equal(input.Length, resampler.Process(input, output));
        Assert.Equal(input, output);
    }
}
