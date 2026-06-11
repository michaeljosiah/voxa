using System.Buffers.Binary;

namespace Voxa.Speech.Piper;

/// <summary>Minimal RIFF/WAVE reader for piper's PCM16 output.</summary>
internal static class WavAudio
{
    public readonly record struct WavInfo(int SampleRate, int Channels, int DataOffset, int DataLength);

    public static WavInfo Parse(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 44 || wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F')
            throw new InvalidDataException("piper output is not a RIFF/WAVE file.");

        int sampleRate = 0, channels = 0;
        int offset = 12; // past "RIFF"<size>"WAVE"
        while (offset + 8 <= wav.Length)
        {
            var isFmt  = wav[offset] == 'f' && wav[offset + 1] == 'm' && wav[offset + 2] == 't' && wav[offset + 3] == ' ';
            var isData = wav[offset] == 'd' && wav[offset + 1] == 'a' && wav[offset + 2] == 't' && wav[offset + 3] == 'a';
            var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(wav.Slice(offset + 4, 4));

            if (isFmt)
            {
                channels   = BinaryPrimitives.ReadInt16LittleEndian(wav.Slice(offset + 10, 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(wav.Slice(offset + 12, 4));
            }
            else if (isData)
            {
                var available = Math.Min(chunkSize, wav.Length - offset - 8);
                return new WavInfo(sampleRate, channels, offset + 8, available);
            }
            offset += 8 + chunkSize + (chunkSize & 1);
        }
        throw new InvalidDataException("piper output has no data chunk.");
    }

    /// <summary>
    /// True when the file's RIFF size field accounts for the whole file — i.e. piper finished
    /// writing it. The fallback completion detector for platforms where piper's stdout
    /// acknowledgment is block-buffered.
    /// </summary>
    public static bool FileIsComplete(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 44) return false;
            Span<byte> header = stackalloc byte[8];
            if (fs.Read(header) != 8) return false;
            if (header[0] != 'R' || header[1] != 'I' || header[2] != 'F' || header[3] != 'F') return false;
            var riffSize = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4, 4));
            return fs.Length >= riffSize + 8;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
