using Voxa.Studio.Audio;

namespace Voxa.Studio.Tests;

/// <summary>
/// VST-001 WS1-A4: the device layer's resampler — hardware 48 kHz ↔ pipeline 16 kHz — preserves
/// signal energy within tolerance and is continuous across chunk boundaries (no clicks).
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
        var input = Sine(16000, 200, 0.1);
        var output = new short[input.Length];
        Assert.Equal(input.Length, resampler.Process(input, output));
        Assert.Equal(input, output);
    }
}
