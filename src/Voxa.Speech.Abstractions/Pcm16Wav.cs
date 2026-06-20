using System.Buffers.Binary;

namespace Voxa.Speech;

/// <summary>
/// Minimal shared PCM16 RIFF/WAVE helper (CQ-009): the canonical 44-byte mono/N-channel header writer
/// and a robust chunk-walking reader, so the half-dozen sites that wrap or parse PCM16 WAV (REST STT, Piper,
/// smart-turn, the CLI, MCP, Studio) don't each re-derive RIFF chunk padding / extensible-fmt handling.
/// Not a codec — PCM16 only; readers keep their own copy/mix/resample logic on top of <see cref="FindData"/>.
/// </summary>
public static class Pcm16Wav
{
    /// <summary>Header size of a canonical PCM WAV (RIFF + fmt(16) + data header), before the sample bytes.</summary>
    public const int HeaderSize = 44;

    /// <summary>The fmt header fields plus the byte range of the <c>data</c> chunk found by <see cref="FindData"/>.</summary>
    public readonly record struct WavFormat(int SampleRate, int Channels, int Bits, int Format, int DataOffset, int DataLength);

    /// <summary>Allocate a complete WAV (header + samples) wrapping <paramref name="pcm"/> as 16-bit PCM.</summary>
    public static byte[] Wrap(ReadOnlySpan<byte> pcm, int sampleRate, int channels = 1)
    {
        var wav = new byte[HeaderSize + pcm.Length];
        WriteHeader(wav, pcm.Length, sampleRate, channels);
        pcm.CopyTo(wav.AsSpan(HeaderSize));
        return wav;
    }

    /// <summary>Write the 44-byte PCM16 header into <paramref name="dest"/> (length ≥ 44) for the given payload.</summary>
    public static void WriteHeader(Span<byte> dest, int pcmLength, int sampleRate, int channels = 1)
    {
        const int bitsPerSample = 16;
        int byteRate = sampleRate * channels * (bitsPerSample / 8);
        int blockAlign = channels * (bitsPerSample / 8);

        "RIFF"u8.CopyTo(dest);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[4..], (uint)(36 + pcmLength));
        "WAVE"u8.CopyTo(dest[8..]);
        "fmt "u8.CopyTo(dest[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[16..], 16);              // PCM fmt chunk size
        BinaryPrimitives.WriteUInt16LittleEndian(dest[20..], 1);               // audio format = PCM
        BinaryPrimitives.WriteUInt16LittleEndian(dest[22..], (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[24..], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[28..], (uint)byteRate);
        BinaryPrimitives.WriteUInt16LittleEndian(dest[32..], (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(dest[34..], bitsPerSample);
        "data"u8.CopyTo(dest[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[40..], (uint)pcmLength);
    }

    /// <summary>
    /// Walk the RIFF chunks and return the <c>fmt </c> fields plus the <c>data</c> chunk's offset/length —
    /// skipping unknown chunks, honoring word alignment, reading the bit depth past any fmt extension
    /// (WAVE_FORMAT_EXTENSIBLE), and clamping an over-declared final chunk to what's present. Zero-copy: the
    /// caller slices <paramref name="wav"/> at <c>DataOffset</c>/<c>DataLength</c> and copies/mixes as it needs.
    /// </summary>
    /// <exception cref="InvalidDataException">The input is not a RIFF/WAVE file, has no <c>data</c>
    /// chunk, or reaches <c>data</c> before a valid <c>fmt </c> chunk.</exception>
    public static WavFormat FindData(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 12 || !wav[..4].SequenceEqual("RIFF"u8) || !wav.Slice(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Not a RIFF/WAVE file.");

        int sampleRate = 0, channels = 0, bits = 0, format = 0;
        bool sawFmt = false;
        for (int offset = 12; offset + 8 <= wav.Length;)
        {
            var id = wav.Slice(offset, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(wav.Slice(offset + 4, 4));
            int payload = offset + 8;
            if (size < 0 || payload + size > wav.Length) size = wav.Length - payload; // truncated / over-declared final chunk

            if (id.SequenceEqual("fmt "u8) && size >= 16)
            {
                format     = BinaryPrimitives.ReadInt16LittleEndian(wav.Slice(payload, 2));
                channels   = BinaryPrimitives.ReadInt16LittleEndian(wav.Slice(payload + 2, 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(wav.Slice(payload + 4, 4));
                bits       = BinaryPrimitives.ReadInt16LittleEndian(wav.Slice(payload + 14, 2));
                sawFmt = true;
            }
            else if (id.SequenceEqual("data"u8))
            {
                // A data chunk before a valid fmt (or after a too-short fmt this loop skipped) would
                // yield zeroed SampleRate/Channels/Bits — reject rather than hand callers bogus metadata,
                // matching how the concrete readers fail on malformed RIFF.
                if (!sawFmt)
                    throw new InvalidDataException("WAV 'data' chunk precedes a valid 'fmt ' chunk.");
                return new WavFormat(sampleRate, channels, bits, format, payload, size);
            }

            offset = payload + size + (size & 1); // chunks are word-aligned
        }

        throw new InvalidDataException("WAV file has no 'data' chunk.");
    }
}
