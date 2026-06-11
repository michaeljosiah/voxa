using System.Diagnostics;
using Voxa.Speech;
using Voxa.Speech.Piper;

namespace Voxa.Speech.Piper.Tests;

/// <summary>
/// Real-binary integration tests (VLS-001 WS2.4). Excluded from the default suite by the
/// LocalModels trait. First local run downloads the pinned piper build (~20 MB) and
/// en_US-amy-low (~60 MB) into the user cache.
/// </summary>
public class PiperIntegrationTests
{
    [Fact]
    [Trait("Category", "LocalModels")]
    public async Task AmyLow_Synthesizes_Real_Audio_And_Leaves_No_Orphans()
    {
        var cache = new VoxaModelCache(
            new VoxaModelCacheOptions(VoxaModelCacheOptions.DefaultCacheRoot(), Offline: false));
        var options = new PiperOptions { Voice = "en_US-amy-low", MaxProcesses = 1 };

        try
        {
            await using var engine = new PiperTtsEngine(options, cache);
            await engine.StartAsync(CancellationToken.None);

            // Cold call (includes process spawn + voice load).
            var first = await CollectPcmAsync(engine, "Hello from Voxa.");

            // amy-low is 16 kHz: sanity-check duration is in a sane band for a short sentence.
            var seconds = first.Length / 2.0 / 16000.0;
            Assert.InRange(seconds, 0.5, 10.0);
            Assert.Contains(first, b => b != 0); // actual audio, not digital silence

            // Warm call on the live host must be fast — the whole point of pooling.
            var sw = Stopwatch.StartNew();
            var second = await CollectPcmAsync(engine, "Second sentence, warm process.");
            sw.Stop();
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2),
                $"warm synthesis took {sw.Elapsed.TotalSeconds:F1}s");
            Assert.NotEmpty(second);
        }
        finally
        {
            // Orphan check: tearing down the pool must leave no piper process behind.
            PiperProcessPool.DisposeAll();
        }

        await Task.Delay(200); // give the OS a beat to reap
        Assert.Empty(Process.GetProcessesByName("piper"));
    }

    private static async Task<byte[]> CollectPcmAsync(PiperTtsEngine engine, string text)
    {
        using var ms = new MemoryStream();
        await foreach (var chunk in engine.SynthesizeAsync(text, CancellationToken.None))
            ms.Write(chunk.Span);
        return ms.ToArray();
    }
}
