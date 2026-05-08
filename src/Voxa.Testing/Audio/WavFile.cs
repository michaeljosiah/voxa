using System.Buffers.Binary;
using System.Text;

namespace Voxa.Testing.Audio;

/// <summary>
/// Minimal RIFF/WAV reader and writer for 16-bit PCM audio. Sufficient for fixture-based
/// integration testing — not a general-purpose audio codec.
/// </summary>
public static class WavFile
{
    /// <summary>Decoded WAV payload. <see cref="Pcm"/> is little-endian 16-bit signed PCM.</summary>
    public sealed record WavData(byte[] Pcm, int SampleRate, int Channels);

    /// <summary>Read a 16-bit PCM WAV file from disk.</summary>
    public static WavData Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return Parse(bytes);
    }

    /// <summary>Parse 16-bit PCM WAV bytes from memory.</summary>
    public static WavData Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 44) throw new InvalidDataException("WAV file too small.");
        if (!bytes[..4].SequenceEqual("RIFF"u8)) throw new InvalidDataException("Not a RIFF file.");
        if (!bytes.Slice(8, 4).SequenceEqual("WAVE"u8)) throw new InvalidDataException("Not a WAVE file.");

        int sampleRate = 0;
        int channels = 0;
        int bitsPerSample = 0;
        int dataOffset = -1;
        int dataSize = 0;
        int offset = 12;

        while (offset + 8 <= bytes.Length)
        {
            var chunkId = bytes.Slice(offset, 4);
            int chunkSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset + 4, 4));
            int payload = offset + 8;

            if (chunkId.SequenceEqual("fmt "u8))
            {
                channels = BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(payload + 2, 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(payload + 4, 4));
                bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(payload + 14, 2));
            }
            else if (chunkId.SequenceEqual("data"u8))
            {
                dataOffset = payload;
                dataSize = chunkSize;
                break;
            }

            offset = payload + chunkSize;
            if ((chunkSize & 1) != 0) offset++;
        }

        if (dataOffset < 0) throw new InvalidDataException("No 'data' chunk found.");
        if (bitsPerSample != 16) throw new NotSupportedException($"Only 16-bit PCM supported (got {bitsPerSample}).");

        var pcm = bytes.Slice(dataOffset, dataSize).ToArray();
        return new WavData(pcm, sampleRate, channels);
    }

    /// <summary>Write 16-bit PCM as a WAV file.</summary>
    public static void Write(string path, ReadOnlySpan<byte> pcm, int sampleRate, int channels)
    {
        const int bitsPerSample = 16;
        int byteRate = sampleRate * channels * (bitsPerSample / 8);
        int blockAlign = channels * (bitsPerSample / 8);
        int dataSize = pcm.Length;
        int fileSize = 36 + dataSize;

        using var fs = File.Create(path);
        using var writer = new BinaryWriter(fs, Encoding.ASCII, leaveOpen: false);

        writer.Write("RIFF"u8);
        writer.Write(fileSize);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);                    // Subchunk1Size for PCM
        writer.Write((short)1);              // PCM format
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write((short)blockAlign);
        writer.Write((short)bitsPerSample);
        writer.Write("data"u8);
        writer.Write(dataSize);
        writer.Write(pcm);
    }
}
