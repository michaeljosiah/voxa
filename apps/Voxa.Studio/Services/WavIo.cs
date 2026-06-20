using System.Buffers.Binary;
using Voxa.Speech;
using Voxa.Studio.Audio;

namespace Voxa.Studio.Services;

/// <summary>
/// Minimal RIFF/WAV reader-writer for the playgrounds: 16-bit PCM in, mono-ized and resampled to
/// whatever rate the consumer needs (Whisper requires 16 kHz). Not a codec — anything that isn't
/// plain PCM16 throws with a message naming the file, never a silent wrong answer.
/// </summary>
internal static class WavIo
{
    public sealed record WavPcm(byte[] Pcm, int SampleRate);

    /// <summary>Read a WAV file as PCM16 mono at <paramref name="targetRate"/>.</summary>
    public static WavPcm ReadMono(string path, int targetRate)
    {
        var bytes = File.ReadAllBytes(path);
        var span = bytes.AsSpan();
        if (span.Length < 44 || !span[..4].SequenceEqual("RIFF"u8) || !span.Slice(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException($"'{Path.GetFileName(path)}' is not a RIFF/WAVE file.");

        int channels = 0, sampleRate = 0, bits = 0, format = 0;
        ReadOnlySpan<byte> data = default;
        for (int offset = 12; offset + 8 <= span.Length;)
        {
            var id = span.Slice(offset, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset + 4, 4));
            int payload = offset + 8;
            if (payload + size > span.Length) size = span.Length - payload;

            if (id.SequenceEqual("fmt "u8))
            {
                format = BinaryPrimitives.ReadInt16LittleEndian(span.Slice(payload, 2));
                channels = BinaryPrimitives.ReadInt16LittleEndian(span.Slice(payload + 2, 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(payload + 4, 4));
                bits = BinaryPrimitives.ReadInt16LittleEndian(span.Slice(payload + 14, 2));
            }
            else if (id.SequenceEqual("data"u8))
            {
                data = span.Slice(payload, size);
                break;
            }

            offset = payload + size + (size & 1);
        }

        if (data.IsEmpty) throw new InvalidDataException($"'{Path.GetFileName(path)}' has no data chunk.");
        if (format != 1 || bits != 16)
            throw new NotSupportedException(
                $"'{Path.GetFileName(path)}' is not 16-bit PCM (format {format}, {bits}-bit). Convert it first.");
        if (channels < 1) throw new InvalidDataException($"'{Path.GetFileName(path)}' declares {channels} channels.");

        var mono = channels == 1 ? data.ToArray() : MixDown(data, channels);
        if (sampleRate == targetRate) return new WavPcm(mono, targetRate);

        // Voice-bandwidth linear resample — same quality bar as the live capture path.
        var resampler = new LinearResampler(sampleRate, targetRate);
        var input = new short[mono.Length / 2];
        Buffer.BlockCopy(mono, 0, input, 0, input.Length * 2);
        var output = new short[resampler.MaxOutputSamples(input.Length)];
        int written = resampler.Process(input, output);
        var result = new byte[written * 2];
        Buffer.BlockCopy(output, 0, result, 0, result.Length);
        return new WavPcm(result, targetRate);
    }

    private static byte[] MixDown(ReadOnlySpan<byte> interleaved, int channels)
    {
        int frames = interleaved.Length / (2 * channels);
        var mono = new byte[frames * 2];
        for (int f = 0; f < frames; f++)
        {
            int sum = 0;
            for (int c = 0; c < channels; c++)
                sum += BinaryPrimitives.ReadInt16LittleEndian(interleaved.Slice((f * channels + c) * 2, 2));
            BinaryPrimitives.WriteInt16LittleEndian(mono.AsSpan(f * 2, 2), (short)(sum / channels));
        }
        return mono;
    }

    /// <summary>PCM16 mono → WAV bytes.</summary>
    public static byte[] Write(byte[] pcm, int sampleRate) => Pcm16Wav.Wrap(pcm, sampleRate); // shared header (CQ-009)
}
