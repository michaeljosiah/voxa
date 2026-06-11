using System.Buffers.Binary;
using Voxa.Speech;
using Voxa.Speech.WhisperCpp;

namespace Voxa.Speech.WhisperCpp.Tests;

/// <summary>
/// Real-model integration tests (VLS-001 WS1.4). Excluded from the default suite by the
/// LocalModels trait; the CI lane restores the model cache, then runs these with network blocked.
/// First local run downloads ggml-tiny.en (~75 MB) into the user cache.
/// </summary>
public class WhisperCppIntegrationTests
{
    /// <summary>Minimal RIFF reader for the canonical 16 kHz mono PCM16 fixtures.</summary>
    private static byte[] ReadWavPcm(string path, out int sampleRate)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 44 || bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F')
            throw new InvalidDataException($"{path} is not a RIFF/WAVE file.");

        sampleRate = 0;
        int offset = 12; // past RIFF header + WAVE tag
        while (offset + 8 <= bytes.Length)
        {
            var chunkId = System.Text.Encoding.ASCII.GetString(bytes, offset, 4);
            var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            if (chunkId == "fmt ")
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 12, 4));
            else if (chunkId == "data")
                return bytes.AsSpan(offset + 8, chunkSize).ToArray();
            offset += 8 + chunkSize + (chunkSize & 1);
        }
        throw new InvalidDataException($"{path} has no data chunk.");
    }

    [Fact]
    [Trait("Category", "LocalModels")]
    public async Task TinyEn_Transcribes_The_Jfk_Fixture()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "jfk.wav");
        var pcm = ReadWavPcm(fixture, out var rate);
        Assert.Equal(WhisperCppSttEngine.RequiredSampleRate, rate);

        var cache = new VoxaModelCache(
            new VoxaModelCacheOptions(VoxaModelCacheOptions.DefaultCacheRoot(), Offline: false));

        await using var engine = new WhisperCppSttEngine(
            new WhisperCppOptions { Model = "tiny.en", Language = "en" }, cache);
        await engine.StartAsync(CancellationToken.None);

        var collector = Task.Run(async () =>
        {
            var results = new List<TranscriptionResult>();
            await foreach (var r in engine.ReadTranscriptsAsync(CancellationToken.None))
                results.Add(r);
            return results;
        });

        // Stream like a transport would: 20 ms frames.
        const int frameBytes = 2 * WhisperCppSttEngine.RequiredSampleRate / 50;
        for (int i = 0; i < pcm.Length; i += frameBytes)
        {
            var len = Math.Min(frameBytes, pcm.Length - i);
            await engine.WriteAudioAsync(pcm.AsMemory(i, len), CancellationToken.None);
        }
        await engine.FlushAsync();
        await engine.StopAsync();

        var results = await collector;
        var transcript = string.Join(" ", results.Select(r => r.Text));

        // Containment, not exact text — model updates must not flake the suite.
        Assert.Contains("country", transcript, StringComparison.OrdinalIgnoreCase);
        Assert.All(results, r => Assert.True(r.IsFinal));
    }
}
