using System.Text;

namespace Voxa.Cli;

/// <summary>
/// Minimal RIFF/WAVE PCM reader and writer — just enough for <c>voxa transcribe</c> (read) and
/// <c>voxa say</c> (write). Deliberately tiny: the CLI is the only consumer and the pipeline speaks
/// raw PCM16 internally, so there is no need for a general audio library.
/// </summary>
internal static class WavIO
{
    /// <summary>The parsed format header plus the raw PCM data bytes of a WAV file.</summary>
    internal readonly record struct WavData(byte[] Pcm, int SampleRate, short Channels, short Bits, short Format);

    /// <summary>Read a WAV file's <c>fmt </c> header and <c>data</c> bytes. Does not resample or convert.</summary>
    public static WavData ReadPcm(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);

        if (ReadFourCc(br) != "RIFF") throw new InvalidDataException("Not a RIFF/WAV file.");
        br.ReadUInt32(); // RIFF chunk size
        if (ReadFourCc(br) != "WAVE") throw new InvalidDataException("Not a WAVE file.");

        short format = 0, channels = 0, bits = 0;
        int sampleRate = 0;
        byte[]? data = null;

        while (fs.Position + 8 <= fs.Length)
        {
            var id = ReadFourCc(br);
            var size = br.ReadInt32();
            if (size < 0) break;

            if (id == "fmt ")
            {
                format = br.ReadInt16();
                channels = br.ReadInt16();
                sampleRate = br.ReadInt32();
                br.ReadInt32();  // byte rate
                br.ReadInt16();  // block align
                bits = br.ReadInt16();
                if (size > 16) br.ReadBytes(size - 16); // skip any extension (e.g. WAVE_FORMAT_EXTENSIBLE)
            }
            else if (id == "data")
            {
                data = br.ReadBytes(size);
            }
            else
            {
                br.ReadBytes(size); // unknown chunk — skip
            }

            if ((size & 1) == 1 && fs.Position < fs.Length) br.ReadByte(); // chunks are word-aligned
        }

        if (data is null) throw new InvalidDataException("WAV file has no 'data' chunk.");
        return new WavData(data, sampleRate, channels, bits, format);
    }

    /// <summary>Write mono 16-bit PCM as a WAV file at <paramref name="sampleRate"/>.</summary>
    public static async Task WritePcm16Async(string path, byte[] pcm, int sampleRate, CancellationToken ct)
    {
        const short channels = 1, bits = 16;
        var byteRate = sampleRate * channels * bits / 8;
        var blockAlign = (short)(channels * bits / 8);

        using var header = new MemoryStream(44);
        using (var w = new BinaryWriter(header, Encoding.ASCII, leaveOpen: true))
        {
            w.Write("RIFF"u8.ToArray());
            w.Write(36 + pcm.Length);
            w.Write("WAVE"u8.ToArray());
            w.Write("fmt "u8.ToArray());
            w.Write(16);            // PCM fmt chunk size
            w.Write((short)1);      // audio format = PCM
            w.Write(channels);
            w.Write(sampleRate);
            w.Write(byteRate);
            w.Write(blockAlign);
            w.Write(bits);
            w.Write("data"u8.ToArray());
            w.Write(pcm.Length);
        }

        await using var fs = File.Create(path);
        await fs.WriteAsync(header.GetBuffer().AsMemory(0, (int)header.Length), ct).ConfigureAwait(false);
        await fs.WriteAsync(pcm, ct).ConfigureAwait(false);
    }

    private static string ReadFourCc(BinaryReader br) => Encoding.ASCII.GetString(br.ReadBytes(4));
}
