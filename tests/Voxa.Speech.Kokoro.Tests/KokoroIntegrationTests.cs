using System.Diagnostics;
using Voxa.Speech;
using Voxa.Speech.Kokoro;

namespace Voxa.Speech.Kokoro.Tests;

/// <summary>
/// Real-model integration tests (VLS-001 WS3.4). Excluded from the default suite by the
/// LocalModels trait. First local run downloads the int8 model (~92 MB), the af_heart voice
/// (~0.5 MB), and the pinned espeak-ng build (~25–60 MB) into the user cache.
/// </summary>
public class KokoroIntegrationTests
{
    [Fact]
    [Trait("Category", "LocalModels")]
    public async Task Int8_AfHeart_Synthesizes_Real_Audio()
    {
        var cache = new VoxaModelCache(
            new VoxaModelCacheOptions(VoxaModelCacheOptions.DefaultCacheRoot(), Offline: false));
        var options = new KokoroOptions { Voice = "af_heart", Precision = "int8" };

        await using var engine = new KokoroTtsEngine(options, cache);
        await engine.StartAsync(CancellationToken.None);

        // Cold call (espeak spawn + first ONNX run).
        var first = await CollectPcmAsync(engine, "Hello from Voxa.");
        var seconds = first.Length / 2.0 / KokoroCatalog.OutputSampleRate;
        Assert.InRange(seconds, 0.5, 10.0);
        Assert.Contains(first, b => b != 0); // actual audio, not digital silence

        // Warm call: session is loaded, espeak is per-call but tiny.
        var sw = Stopwatch.StartNew();
        var second = await CollectPcmAsync(engine, "Second sentence, warm session.");
        sw.Stop();
        Assert.NotEmpty(second);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"warm synthesis took {sw.Elapsed.TotalSeconds:F1}s");
    }

    private static async Task<byte[]> CollectPcmAsync(KokoroTtsEngine engine, string text)
    {
        using var ms = new MemoryStream();
        await foreach (var chunk in engine.SynthesizeAsync(text, CancellationToken.None))
            ms.Write(chunk.Span);
        return ms.ToArray();
    }
}
