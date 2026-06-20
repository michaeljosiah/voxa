using System.Buffers.Binary;
using Voxa.Speech;

namespace Voxa.Speech.Abstractions.Tests;

/// <summary>
/// The shared PCM16 WAV helper (CQ-009) the REST STT engines, smart-turn, the CLI, and Studio now use instead
/// of hand-rolling the RIFF header / chunk walk: <see cref="Pcm16Wav.Wrap"/> round-trips through
/// <see cref="Pcm16Wav.FindData"/>, and the reader skips unknown chunks and rejects non-WAV input.
/// </summary>
public class Pcm16WavTests
{
    [Fact]
    public void Wrap_Then_FindData_Round_Trips_Format_And_Data()
    {
        var pcm = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var wav = Pcm16Wav.Wrap(pcm, 16000);

        Assert.Equal(Pcm16Wav.HeaderSize + pcm.Length, wav.Length);
        var fmt = Pcm16Wav.FindData(wav);
        Assert.Equal(16000, fmt.SampleRate);
        Assert.Equal(1, fmt.Channels);
        Assert.Equal(16, fmt.Bits);
        Assert.Equal(1, fmt.Format); // PCM
        Assert.Equal(Pcm16Wav.HeaderSize, fmt.DataOffset);
        Assert.Equal(pcm.Length, fmt.DataLength);
        Assert.Equal(pcm, wav.AsSpan(fmt.DataOffset, fmt.DataLength).ToArray());
    }

    [Fact]
    public void Wrap_Writes_Channel_Count_Into_The_Header()
    {
        var fmt = Pcm16Wav.FindData(Pcm16Wav.Wrap(new byte[8], 24000, channels: 2));
        Assert.Equal(2, fmt.Channels);
        Assert.Equal(24000, fmt.SampleRate);
    }

    [Fact]
    public void FindData_Skips_An_Unknown_Word_Aligned_Chunk_Before_Data()
    {
        var pcm = new byte[] { 0xAA, 0xBB };
        var wav = BuildWavWithLeadingJunkChunk(pcm, sampleRate: 16000, junk: [0x01, 0x02, 0x03]); // odd → padded

        var fmt = Pcm16Wav.FindData(wav);
        Assert.Equal(16000, fmt.SampleRate);
        Assert.Equal(pcm, wav.AsSpan(fmt.DataOffset, fmt.DataLength).ToArray()); // found the real data past the junk
    }

    [Fact]
    public void FindData_Throws_On_Non_Riff_Input()
    {
        Assert.Throws<InvalidDataException>(() => Pcm16Wav.FindData(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 }));
    }

    [Fact]
    public void FindData_Clamps_An_Over_Declared_Data_Chunk()
    {
        var wav = Pcm16Wav.Wrap(new byte[4], 16000);                    // 44-byte header + 4 data bytes
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(40), 1000); // data chunk lies: claims 1000 bytes
        var fmt = Pcm16Wav.FindData(wav);
        Assert.Equal(4, fmt.DataLength);                               // clamped to the bytes actually present
    }

    [Fact]
    public void FindData_Reads_Bit_Depth_Past_A_Fmt_Extension()
    {
        // fmt chunk of size 18 (16 standard fields + a 2-byte cbSize, as WAVEFORMATEX appends): the reader must
        // read bits at the fixed offset and still advance past the full declared fmt size to reach `data`.
        var pcm = new byte[] { 7, 8 };
        var b = new byte[12 + (8 + 18) + (8 + pcm.Length)];
        int o = 0;
        o += Tag(b, o, "RIFF"u8); o += U32(b, o, (uint)(b.Length - 8)); o += Tag(b, o, "WAVE"u8);
        o += Tag(b, o, "fmt "u8); o += U32(b, o, 18);
        o += U16(b, o, 1); o += U16(b, o, 1); o += U32(b, o, 16000); o += U32(b, o, 32000); o += U16(b, o, 2); o += U16(b, o, 16);
        o += U16(b, o, 0); // cbSize extension
        o += Tag(b, o, "data"u8); o += U32(b, o, (uint)pcm.Length); pcm.CopyTo(b.AsSpan(o));

        var fmt = Pcm16Wav.FindData(b);
        Assert.Equal(16, fmt.Bits);
        Assert.Equal(16000, fmt.SampleRate);
        Assert.Equal(pcm, b.AsSpan(fmt.DataOffset, fmt.DataLength).ToArray());

        static int Tag(byte[] d, int at, ReadOnlySpan<byte> t) { t.CopyTo(d.AsSpan(at)); return t.Length; }
        static int U32(byte[] d, int at, uint v) { BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(at), v); return 4; }
        static int U16(byte[] d, int at, ushort v) { BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(at), v); return 2; }
    }

    // RIFF/WAVE → fmt(16) → an unknown "JUNK" chunk (odd size → word-aligned) → data. Exercises skip + alignment.
    private static byte[] BuildWavWithLeadingJunkChunk(byte[] pcm, int sampleRate, byte[] junk)
    {
        int junkPadded = junk.Length + (junk.Length & 1);
        var b = new byte[12 + (8 + 16) + (8 + junkPadded) + (8 + pcm.Length)];
        int o = 0;
        o += Tag(b, o, "RIFF"u8); o += U32(b, o, (uint)(b.Length - 8)); o += Tag(b, o, "WAVE"u8);
        o += Tag(b, o, "fmt "u8); o += U32(b, o, 16); o += U16(b, o, 1); o += U16(b, o, 1);
        o += U32(b, o, (uint)sampleRate); o += U32(b, o, (uint)(sampleRate * 2)); o += U16(b, o, 2); o += U16(b, o, 16);
        o += Tag(b, o, "JUNK"u8); o += U32(b, o, (uint)junk.Length); junk.CopyTo(b.AsSpan(o)); o += junkPadded;
        o += Tag(b, o, "data"u8); o += U32(b, o, (uint)pcm.Length); pcm.CopyTo(b.AsSpan(o));
        return b;

        static int Tag(byte[] d, int at, ReadOnlySpan<byte> t) { t.CopyTo(d.AsSpan(at)); return t.Length; }
        static int U32(byte[] d, int at, uint v) { BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(at), v); return 4; }
        static int U16(byte[] d, int at, ushort v) { BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(at), v); return 2; }
    }
}
