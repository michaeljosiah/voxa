using System.Buffers.Binary;
using Voxa.Speech.Piper;

namespace Voxa.Speech.Piper.Tests;

/// <summary>
/// Unit coverage for the engine over the fake WAV-source seam — no piper binary, no model.
/// </summary>
public class PiperTtsEngineTests
{
    /// <summary>Build a minimal valid PCM16 WAV.</summary>
    internal static byte[] Wav(int sampleRate, int samples, short value = 1000)
    {
        var dataBytes = samples * 2;
        var wav = new byte[44 + dataBytes];
        "RIFF"u8.CopyTo(wav);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), 36 + dataBytes);
        "WAVE"u8.CopyTo(wav.AsSpan(8));
        "fmt "u8.CopyTo(wav.AsSpan(12));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1);  // PCM
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), 1);  // mono
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), sampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 16);
        "data"u8.CopyTo(wav.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), dataBytes);
        for (int i = 0; i < samples; i++)
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(44 + i * 2), value);
        return wav;
    }

    private static async Task<List<byte[]>> CollectChunksAsync(PiperTtsEngine engine, string text)
    {
        var chunks = new List<byte[]>();
        await foreach (var chunk in engine.SynthesizeAsync(text, CancellationToken.None))
            chunks.Add(chunk.ToArray());
        return chunks;
    }

    [Fact]
    public async Task Strips_The_Wav_Header_And_Yields_8KiB_Chunks()
    {
        const int samples = 10_000; // 20 000 bytes → 2×8192 + 3616
        var engine = new PiperTtsEngine(
            new PiperOptions { Voice = "en_US-lessac-medium" },
            (_, _) => Task.FromResult(Wav(22050, samples)));

        var chunks = await CollectChunksAsync(engine, "hello");

        Assert.Equal([8192, 8192, 3616], chunks.Select(c => c.Length).ToArray());
        Assert.Equal(samples * 2, chunks.Sum(c => c.Length));
        // Every byte is PCM payload (value 1000 little-endian), no RIFF header residue.
        Assert.All(chunks, c =>
        {
            Assert.Equal(0xE8, c[0]); // 1000 = 0x03E8
            Assert.Equal(0x03, c[1]);
        });
    }

    [Fact]
    public async Task Wrong_Rate_From_Piper_Names_The_Config_Key()
    {
        // Voice name infers 16 000 (amy-low); the fake piper produces 22 050.
        var engine = new PiperTtsEngine(
            new PiperOptions { Voice = "en_US-amy-low" },
            (_, _) => Task.FromResult(Wav(22050, 1000)));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CollectChunksAsync(engine, "hello"));

        Assert.Contains("22050", ex.Message);
        Assert.Contains("16000", ex.Message);
        Assert.Contains("Voxa:Piper:OutputSampleRate", ex.Message);
    }

    [Fact]
    public async Task Explicit_Rate_Override_Wins_Over_Name_Inference()
    {
        var engine = new PiperTtsEngine(
            new PiperOptions { Voice = "en_US-amy-low", OutputSampleRate = 22050 },
            (_, _) => Task.FromResult(Wav(22050, 100)));

        var chunks = await CollectChunksAsync(engine, "hello");
        Assert.Equal(200, chunks.Sum(c => c.Length));
    }

    [Fact]
    public async Task NonWav_Output_Is_A_Loud_Error()
    {
        var engine = new PiperTtsEngine(
            new PiperOptions(),
            (_, _) => Task.FromResult("not a wav at all, sorry"u8.ToArray()));

        await Assert.ThrowsAsync<InvalidDataException>(() => CollectChunksAsync(engine, "hello"));
    }

    [Fact]
    public void WavAudio_FileIsComplete_Detects_Truncation()
    {
        var dir = Path.Combine(Path.GetTempPath(), "voxa-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var complete = Path.Combine(dir, "complete.wav");
            File.WriteAllBytes(complete, Wav(16000, 1000));
            Assert.True(WavAudio.FileIsComplete(complete));

            var truncated = Path.Combine(dir, "truncated.wav");
            File.WriteAllBytes(truncated, Wav(16000, 1000).AsSpan(0, 500).ToArray());
            Assert.False(WavAudio.FileIsComplete(truncated));

            Assert.False(WavAudio.FileIsComplete(Path.Combine(dir, "missing.wav")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
