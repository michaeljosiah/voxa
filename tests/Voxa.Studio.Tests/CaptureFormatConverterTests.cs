using System.Threading.Channels;
using NAudio.Wave;
using Voxa.Studio.Audio;

namespace Voxa.Studio.Tests;

/// <summary>
/// Regression coverage for the WASAPI mix-format matrix. Real endpoints usually expose
/// <c>WAVE_FORMAT_EXTENSIBLE</c> (with a float or PCM subformat GUID) rather than the plain
/// encodings — a converter that only accepted plain <c>IeeeFloat</c>/<c>Pcm</c> dropped every
/// buffer on those devices and Studio appeared deaf. Every supported format must yield frames;
/// an unsupported one must throw at construction (loud beats silent).
/// </summary>
public class CaptureFormatConverterTests
{
    private const int TargetRate = 16000;

    public static TheoryData<string> SupportedFormats => new()
    {
        "float32-plain", "float32-extensible", "pcm16-plain", "pcm16-extensible", "pcm24-plain", "pcm32-extensible",
    };

    private static WaveFormat FormatFor(string name) => name switch
    {
        "float32-plain" => WaveFormat.CreateIeeeFloatWaveFormat(48000, 2),
        "float32-extensible" => new WaveFormatExtensible(48000, 32, 2),
        "pcm16-plain" => new WaveFormat(44100, 16, 2),
        "pcm16-extensible" => new WaveFormatExtensible(48000, 16, 2),
        "pcm24-plain" => new WaveFormat(48000, 24, 1),
        "pcm32-extensible" => new WaveFormatExtensible(48000, 32, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(SupportedFormats))]
    public void Supported_Mix_Formats_Produce_PCM16_Frames(string formatName)
    {
        var format = FormatFor(formatName);
        var channel = Channel.CreateUnbounded<byte[]>();
        var converter = new CaptureFormatConverter(format, TargetRate, channel.Writer);

        // 100 ms of a loud 440 Hz tone in the device's own encoding.
        converter.Convert(Sine(format, seconds: 0.1));

        // ≥4 full 20 ms frames at 16 kHz mono PCM16, carrying real (non-silent) audio.
        var frames = Drain(channel);
        Assert.True(frames.Count >= 4, $"{formatName}: got {frames.Count} frames");
        Assert.All(frames, f => Assert.Equal(TargetRate / 50 * 2, f.Length));
        var peak = frames.SelectMany(f => Enumerable.Range(0, f.Length / 2)
            .Select(i => Math.Abs((int)BitConverter.ToInt16(f, i * 2)))).Max();
        Assert.True(peak > 8000, $"{formatName}: peak {peak} — audio lost in conversion");
    }

    [Fact]
    public void Unconvertible_Format_Throws_With_Guidance_Instead_Of_Going_Deaf()
    {
        var channel = Channel.CreateUnbounded<byte[]>();
        var alaw = WaveFormat.CreateALawFormat(8000, 1);

        var ex = Assert.Throws<NotSupportedException>(
            () => new CaptureFormatConverter(alaw, TargetRate, channel.Writer));
        Assert.Contains("mix format", ex.Message);
        Assert.Contains("sound settings", ex.Message);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>Encode a 440 Hz tone (amplitude 0.6) in whatever encoding the format declares.</summary>
    private static byte[] Sine(WaveFormat format, double seconds)
    {
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format is WaveFormatExtensible ext &&
                ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"));
        int bytesPerSample = format.BitsPerSample / 8;
        int sampleSets = (int)(format.SampleRate * seconds);
        var buffer = new byte[sampleSets * format.Channels * bytesPerSample];

        for (int s = 0; s < sampleSets; s++)
        {
            double value = Math.Sin(2 * Math.PI * 440 * s / format.SampleRate) * 0.6;
            for (int c = 0; c < format.Channels; c++)
            {
                int offset = (s * format.Channels + c) * bytesPerSample;
                if (isFloat)
                {
                    BitConverter.TryWriteBytes(buffer.AsSpan(offset, 4), (float)value);
                }
                else if (format.BitsPerSample == 16)
                {
                    BitConverter.TryWriteBytes(buffer.AsSpan(offset, 2), (short)(value * short.MaxValue));
                }
                else if (format.BitsPerSample == 24)
                {
                    int scaled = (int)(value * int.MaxValue);
                    buffer[offset] = (byte)(scaled >> 8);
                    buffer[offset + 1] = (byte)(scaled >> 16);
                    buffer[offset + 2] = (byte)(scaled >> 24);
                }
                else // 32-bit integer PCM
                {
                    BitConverter.TryWriteBytes(buffer.AsSpan(offset, 4), (int)(value * int.MaxValue));
                }
            }
        }
        return buffer;
    }

    private static List<byte[]> Drain(Channel<byte[]> channel)
    {
        var frames = new List<byte[]>();
        while (channel.Reader.TryRead(out var frame)) frames.Add(frame);
        return frames;
    }
}
