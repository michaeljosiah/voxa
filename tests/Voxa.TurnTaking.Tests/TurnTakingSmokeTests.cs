using System.Text.Json;
using Voxa.TurnTaking;

namespace Voxa.TurnTaking.Tests;

/// <summary>
/// VRT-001 default-lane gate (Phases A–D): the mock-engine harness over the bundled mini fixture produces
/// parseable per-sample records + summary.csv + score.json for the three cascade-fair categories, discovers
/// + skips <c>backchannel</c>, passes the checked-in baseline, and the scorer's direction is correct. Offline
/// + deterministic — no model download, no network, completes in seconds. The <c>LocalModels</c>-trait test
/// is the real-engine lane (Phase B), excluded from the inner loop.
/// </summary>
public class TurnTakingSmokeTests
{
    private static string MiniFixture => Path.Combine(AppContext.BaseDirectory, "fixtures", "fdb-mini");

    private static TurnTakingHarness.Options MockOptions(string outDir, string? category = null, int? limit = null)
        => new(MiniFixture, outDir, category, limit, Stt: "mock", Tts: "mock", Llm: "Echo");

    private static string TempOut() => Path.Combine(Path.GetTempPath(), "voxa-vrt001-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Mock_Harness_Produces_Parseable_Records_And_Artifacts()
    {
        Assert.True(Directory.Exists(MiniFixture), $"mini fixture missing at {MiniFixture}");
        var outDir = TempOut();
        try
        {
            var result = await TurnTakingHarness.RunAsync(MockOptions(outDir));

            foreach (var category in CorpusWalker.CascadeFairCategories)
            {
                Assert.Contains(result.Records, r => r.Category == category && r.Error is null);
                var json = Path.Combine(outDir, $"{category}__sample_0001.json");
                Assert.True(File.Exists(json), $"missing record {json}");
                using var doc = JsonDocument.Parse(File.ReadAllText(json));
                Assert.Equal(category, doc.RootElement.GetProperty("category").GetString());
            }

            // backchannel discovered + skipped, never scored (VRT-001 §5).
            Assert.Contains(result.Skipped, s => s.StartsWith("backchannel", StringComparison.Ordinal));
            Assert.DoesNotContain(result.Records, r => r.Category == CorpusWalker.SkippedCategory);

            // WS3/WS4 artifacts written.
            Assert.True(File.Exists(Path.Combine(outDir, "summary.csv")));
            Assert.True(File.Exists(Path.Combine(outDir, "score.json")));
        }
        finally { TryDelete(outDir); }
    }

    [Fact]
    public async Task Mock_Run_Passes_The_Checked_In_Baseline_Gate()
    {
        var baselinePath = Path.Combine(AppContext.BaseDirectory, "baseline.json");
        Assert.True(File.Exists(baselinePath), $"baseline missing at {baselinePath}");

        var outDir = TempOut();
        try
        {
            var result = await TurnTakingHarness.RunAsync(MockOptions(outDir));
            var regressions = BaselineGate.Check(result.Scores, Baseline.Load(baselinePath));
            Assert.True(regressions.Count == 0, "unexpected regression(s): " + string.Join("; ", regressions));
        }
        finally { TryDelete(outDir); }
    }

    [Fact] // The gate mechanism VRT-002+ are measured against: an injected regression fails the build.
    public void Baseline_Gate_Catches_An_Injected_Regression()
    {
        var baseline = new Baseline("mini", "mock",
            new BaselineTolerance(Tor: 0.05, LatencyMsPct: 0.20, LatencyMsAbs: 50.0),
            new Dictionary<string, BaselineCategory>
            {
                ["pause_handling"] = new(Tor: 0.0),
                ["smooth_turn_taking"] = new(TtfbP50Ms: 300.0),
                ["user_interruption"] = new(BargeInYieldP50Ms: 100.0),
            });

        var regressed = new[]
        {
            new CategoryScore("pause_handling", Tor: 1.0, null, null, null, Skipped: false),                 // barged in on the pause
            new CategoryScore("smooth_turn_taking", null, TtfbP50Ms: 600.0, null, 0.6, Skipped: false),       // 2× slower reply
            new CategoryScore("user_interruption", null, null, BargeInYieldP50Ms: 500.0, 0.67, Skipped: false), // 5× slower yield
        };

        Assert.Equal(3, BaselineGate.Check(regressed, baseline).Count);   // TOR + ttfb + yield gates all fire
    }

    [Fact] // Codex P1: a green gate must mean the benchmark ran — a baseline-required metric that's missing/null fails.
    public void Baseline_Gate_Fails_When_A_Required_Metric_Is_Absent()
    {
        var baseline = new Baseline("mini", "mock",
            new BaselineTolerance(Tor: 0.05, LatencyMsPct: 0.20, LatencyMsAbs: 50.0),
            new Dictionary<string, BaselineCategory>
            {
                ["pause_handling"] = new(Tor: 0.0),
                ["smooth_turn_taking"] = new(TtfbP50Ms: 300.0),
            });

        // A filtered/empty run that omits smooth_turn_taking entirely.
        var omitted = new[] { new CategoryScore("pause_handling", Tor: 0.0, null, null, null, Skipped: false) };
        Assert.Contains(BaselineGate.Check(omitted, baseline), r => r.Contains("smooth_turn_taking"));

        // A run that produced the category but its required metric went null (a broken stage).
        var nulled = new[]
        {
            new CategoryScore("pause_handling", Tor: 0.0, null, null, null, Skipped: false),
            new CategoryScore("smooth_turn_taking", null, null, null, null, Skipped: false),
        };
        Assert.Contains(BaselineGate.Check(nulled, baseline), r => r.Contains("smooth_turn_taking"));
    }

    [Fact] // Codex P1: user_interruption is scored on barge-in YIELD, not reply latency (else it hides barge-in regressions).
    public async Task User_Interruption_Is_Scored_On_Yield_Not_Reply_Latency()
    {
        var outDir = TempOut();
        try
        {
            var result = await TurnTakingHarness.RunAsync(MockOptions(outDir, category: "user_interruption"));
            var score = result.Scores.Single(s => s.Category == "user_interruption");

            Assert.Null(score.TtfbP50Ms);            // NOT the reply-after-user-stops latency
            Assert.Null(score.BargeInYieldP50Ms);    // offline file-driven harness produces no barge-in → not exercised
        }
        finally { TryDelete(outDir); }
    }

    [Fact] // VRT-001 R5: the scorer's direction is correct — staying silent through the pause scores BETTER (lower TOR).
    public void Scorer_Pause_Handling_TOR_Is_Lower_Better()
    {
        static SampleRecord Rec(int userStoppedEdges) => new(
            "s", "pause_handling", new EngineNames("mock", "Echo", "mock"),
            new SampleTimings(1, 1, 1, 100, 200, null), new SampleTranscripts(null, "x"),
            new TurnSignals(userStoppedEdges, BotStartedEdges: 1), "s.response.wav", null);

        static double Tor(params SampleRecord[] recs)
            => Scorer.Score(recs, Summarizer.Summarize(recs))
                     .Single(s => s.Category == "pause_handling").Tor!.Value;

        Assert.Equal(0.0, Tor(Rec(1)));           // waited through the pause (one turn edge)
        Assert.Equal(1.0, Tor(Rec(2)));           // ended the turn during the pause (premature)
        Assert.True(Tor(Rec(1)) < Tor(Rec(2)));   // silence wins
    }

    [Fact]
    public void Backchannel_Is_Never_In_The_Cascade_Fair_Set()
        => Assert.DoesNotContain(CorpusWalker.SkippedCategory, CorpusWalker.CascadeFairCategories);

    [Fact]
    [Trait("Category", "LocalModels")] // Phase B: the real local-engine lane (downloads models on first run).
    public async Task Real_Local_Engines_Produce_A_Real_Transcript()
    {
        var outDir = TempOut();
        try
        {
            // WhisperCpp tiny.en + Piper en_US-amy-low — the same models the zero-network lane caches
            // (the smallest, per the local-speech e2e test), so this needs no download there.
            var options = new TurnTakingHarness.Options(
                MiniFixture, outDir, Category: "smooth_turn_taking", Limit: 1,
                Stt: "WhisperCpp", Tts: "Piper", Llm: "Echo",
                ExtraConfig: new Dictionary<string, string?>
                {
                    ["Voxa:WhisperCpp:Model"] = "tiny.en",
                    ["Voxa:Piper:Voice"] = "en_US-amy-low",
                });

            var result = await TurnTakingHarness.RunAsync(options);

            var rec = Assert.Single(result.Records);
            Assert.Null(rec.Error);
            Assert.False(string.IsNullOrWhiteSpace(rec.Transcripts.Hypothesis)); // a real Whisper transcript
            Assert.NotNull(rec.ResponseWav);
            Assert.True(new FileInfo(Path.Combine(outDir, rec.ResponseWav!)).Length > 44); // real synthesized audio
        }
        finally { TryDelete(outDir); }
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
    }
}
