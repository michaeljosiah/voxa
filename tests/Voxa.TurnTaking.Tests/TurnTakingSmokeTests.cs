using System.Text.Json;
using Voxa.TurnTaking;

namespace Voxa.TurnTaking.Tests;

/// <summary>
/// VRT-001 default-lane smoke gate (Phase A): the mock-engine harness, run over the bundled mini fixture,
/// produces a parseable per-sample record for each of the three cascade-fair categories, and discovers +
/// skips <c>backchannel</c>. Offline + deterministic — no model download, no network, completes in seconds.
/// </summary>
public class TurnTakingSmokeTests
{
    private static string MiniFixture => Path.Combine(AppContext.BaseDirectory, "fixtures", "fdb-mini");

    [Fact]
    public async Task Mock_Harness_Produces_Parseable_Records_For_The_Cascade_Fair_Categories()
    {
        Assert.True(Directory.Exists(MiniFixture), $"mini fixture missing at {MiniFixture}");

        var outDir = Path.Combine(Path.GetTempPath(), "voxa-vrt001-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new TurnTakingHarness.Options(
                MiniFixture, outDir, Category: null, Limit: null, Stt: "mock", Tts: "mock", Llm: "Echo");

            var result = await TurnTakingHarness.RunAsync(options);

            foreach (var category in CorpusWalker.CascadeFairCategories)
            {
                Assert.Contains(result.Records, r => r.Category == category && r.Error is null);

                var json = Path.Combine(outDir, $"{category}__sample_0001.json");
                Assert.True(File.Exists(json), $"missing record {json}");

                using var doc = JsonDocument.Parse(File.ReadAllText(json));
                Assert.Equal(category, doc.RootElement.GetProperty("category").GetString());
                Assert.True(doc.RootElement.TryGetProperty("timings_ms", out _));
            }

            // backchannel is discovered and logged skipped — never scored (VRT-001 §5).
            Assert.Contains(result.Skipped, s => s.StartsWith("backchannel", StringComparison.Ordinal));
            Assert.DoesNotContain(result.Records, r => r.Category == CorpusWalker.SkippedCategory);
        }
        finally
        {
            try { Directory.Delete(outDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void Backchannel_Is_Never_In_The_Cascade_Fair_Set()
        => Assert.DoesNotContain(CorpusWalker.SkippedCategory, CorpusWalker.CascadeFairCategories);
}
