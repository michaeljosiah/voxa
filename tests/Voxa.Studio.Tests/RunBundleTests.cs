using Voxa.Studio.Services;

namespace Voxa.Studio.Tests;

/// <summary>
/// VST-002 D4: the run bundle — turn assembly from the event stream, nearest-rank percentiles,
/// chunk-span RTF, the rule-based takeaway, compare with context warnings, and the folder store.
/// </summary>
public class RunBundleTests
{
    private static RunEvent Stage(long micros, string stage, double ms) =>
        new() { Micros = micros, Kind = "stage", Stage = stage, Ms = ms };

    /// <summary>One synthetic turn: the five stages closing with audio_out.</summary>
    private static IEnumerable<RunEvent> Turn(long t0, double vad, double stt, double agent, double tts, double @out)
    {
        yield return Stage(t0, "vad_close", vad);
        yield return Stage(t0 + 1000, "stt_final", stt);
        yield return Stage(t0 + 2000, "agent_first_token", agent);
        yield return Stage(t0 + 3000, "tts_first_byte", tts);
        yield return Stage(t0 + 4000, "audio_out", @out);
    }

    [Fact]
    public void Compute_Assembles_Turns_On_Audio_Out()
    {
        var events = Turn(0, 800, 300, 5, 90, 2).Concat(Turn(10_000_000, 400, 250, 4, 80, 1)).ToList();
        var stats = RunStats.Compute(events);

        Assert.Equal(2, stats.TurnCount);
        Assert.Equal(1197, stats.Turns[0].TotalMs, 3);
        Assert.Equal(735, stats.Turns[1].TotalMs, 3);
        Assert.Equal(735, stats.TtfbP50, 3);   // nearest-rank p50 of {735, 1197}
        Assert.Equal(1197, stats.TtfbP95, 3);
        Assert.Equal(1197, stats.TtfbMax, 3);
        Assert.Equal(400, stats.StageP50["vad_close"], 3);
    }

    [Fact]
    public void Percentile_Is_Nearest_Rank_Not_Interpolated()
    {
        double[] values = [100, 200, 300, 400];
        Assert.Equal(200, RunStats.Percentile(values, 0.50)); // rank ceil(.5*4)=2 → 200, no 250
        Assert.Equal(400, RunStats.Percentile(values, 0.95));
        Assert.Equal(100, RunStats.Percentile([100], 0.50));
    }

    [Fact]
    public void Rtf_Spans_The_Whole_Reply_Not_Just_The_Chunks_Before_Audio_Out()
    {
        // The real stream shape: the sink publishes audio_out on the FIRST audio frame of a
        // reply, so a streaming voice's later chunks arrive AFTER the turn's stages closed.
        // Regression: those chunks must still belong to turn 1 — not contaminate turn 2.
        var events = new List<RunEvent>();

        // Turn 1: stages close at the first chunk; two more chunks stream after audio_out.
        // Three chunks spanning 1 s wall producing 2 s of 16 kHz audio → RTF 0.5.
        events.AddRange(Turn(0, 100, 100, 100, 100, 1));
        events.Add(new RunEvent { Micros = 4500, Kind = "tts", Bytes = 32000, SampleRate = 16000 });
        events.Add(new RunEvent { Micros = 504_500, Kind = "tts", Bytes = 16000, SampleRate = 16000 });
        events.Add(new RunEvent { Micros = 1_004_500, Kind = "tts", Bytes = 16000, SampleRate = 16000 });
        events.Add(new RunEvent { Micros = 1_100_000, Kind = "turn", Edge = "BotStopped" });

        // Turn 2: a single-chunk reply of 1 s audio; tts_first_byte was 200 ms → RTF 0.2.
        events.AddRange(Turn(5_000_000, 100, 100, 100, 200, 1));
        events.Add(new RunEvent { Micros = 5_005_000, Kind = "tts", Bytes = 32000, SampleRate = 16000 });
        events.Add(new RunEvent { Micros = 5_100_000, Kind = "turn", Edge = "BotStopped" });

        var stats = RunStats.Compute(events);
        Assert.Equal(0.5, stats.Turns[0].Rtf!.Value, 3); // all three chunks, not just the first
        Assert.Equal(0.2, stats.Turns[1].Rtf!.Value, 3); // uncontaminated by turn 1's tail
        Assert.Equal(0.35, stats.TtsRtfMean!.Value, 3);
    }

    [Fact]
    public void Rtf_Finalizes_On_Interruption_And_At_Stream_End()
    {
        // An interrupted reply still measures what it produced before the cut: two 0.5 s chunks
        // spanning 0.5 s wall → RTF 0.5.
        var events = new List<RunEvent>();
        events.AddRange(Turn(0, 100, 100, 100, 100, 1));
        events.Add(new RunEvent { Micros = 5000, Kind = "tts", Bytes = 16000, SampleRate = 16000 });
        events.Add(new RunEvent { Micros = 505_000, Kind = "tts", Bytes = 16000, SampleRate = 16000 });
        events.Add(new RunEvent { Micros = 600_000, Kind = "turn", Edge = "Interrupted" });

        // A run stopped mid-reply (no BotStopped ever lands) still attributes the tail:
        // a single 1 s chunk → the 250 ms tts_first_byte fallback → RTF 0.25.
        events.AddRange(Turn(3_000_000, 100, 100, 100, 250, 1));
        events.Add(new RunEvent { Micros = 3_005_000, Kind = "tts", Bytes = 32000, SampleRate = 16000 });

        var stats = RunStats.Compute(events);
        Assert.Equal(0.5, stats.Turns[0].Rtf!.Value, 3);
        Assert.Equal(0.25, stats.Turns[1].Rtf!.Value, 3);
        Assert.Equal(1, stats.InterruptionCount);
    }

    [Fact]
    public void Errors_And_Interruptions_Are_Counted()
    {
        var events = new List<RunEvent>
        {
            new() { Kind = "error", Source = "tap", Text = "boom" },
            new() { Kind = "turn", Edge = "Interrupted" },
            new() { Kind = "turn", Edge = "BotStopped" },
        };
        var stats = RunStats.Compute(events);
        Assert.Equal(1, stats.ErrorCount);
        Assert.Equal(1, stats.InterruptionCount);
        Assert.Equal(0, stats.TurnCount);
    }

    [Fact]
    public void Takeaway_Names_The_Dominant_Stage_And_A_Real_Lever()
    {
        // VAD-dominant run → the lever is the real knob name.
        var vadHeavy = new RunBundle { Events = Turn(0, 800, 100, 5, 60, 2).ToList() };
        vadHeavy.Stats = RunStats.Compute(vadHeavy.Events);
        Assert.Contains("VAD hangover", vadHeavy.Takeaway());
        Assert.Contains("StopDurationMs", vadHeavy.Takeaway());
        Assert.Contains("83%", vadHeavy.Takeaway()); // 800 of 967

        var agentHeavy = new RunBundle { Events = Turn(0, 100, 100, 900, 60, 2).ToList() };
        agentHeavy.Stats = RunStats.Compute(agentHeavy.Events);
        Assert.Contains("Agent first-token", agentHeavy.Takeaway());

        // No turns: nothing to measure — and errors take the blame when present.
        var empty = new RunBundle();
        Assert.Contains("No completed turns", empty.Takeaway());
        var failed = new RunBundle { Events = [new RunEvent { Kind = "error", Text = "x" }] };
        failed.Stats = RunStats.Compute(failed.Events);
        Assert.Contains("error", failed.Takeaway());
    }

    [Fact]
    public void Bundle_Json_Roundtrips()
    {
        var bundle = new RunBundle
        {
            Label = "whispercpp·echo·piper",
            StartedAt = new DateTimeOffset(2026, 6, 13, 12, 0, 0, TimeSpan.Zero),
            DurationSeconds = 42.5,
            SourceDescription = "scripted · 2 utterance(s) · 1500 ms gaps",
            Profile = "Quality",
            Config = new() { ["Voxa:Stt"] = "WhisperCpp" },
            Context = new RunContext(16, "TestOS", "X64", ModelsCached: true),
            Events = Turn(0, 800, 300, 5, 90, 2).ToList(),
        };
        bundle.Stats = RunStats.Compute(bundle.Events);

        var restored = RunBundle.FromJson(bundle.ToJson());

        Assert.Equal(bundle.Label, restored.Label);
        Assert.Equal(bundle.Profile, restored.Profile);
        Assert.Equal("WhisperCpp", restored.Config["Voxa:Stt"]);
        Assert.Equal(16, restored.Context.CoreCount);
        Assert.True(restored.Context.ModelsCached);
        Assert.Equal(5, restored.Events.Count);
        Assert.Equal(1, restored.Stats.TurnCount);
        Assert.Equal(bundle.Stats.TtfbP50, restored.Stats.TtfbP50, 3);
    }

    [Fact]
    public void Csv_Has_A_Row_Per_Turn_In_Stage_Order()
    {
        var bundle = new RunBundle { Events = Turn(0, 800, 300, 5, 90, 2).Concat(Turn(9_000_000, 400, 200, 4, 80, 1)).ToList() };
        bundle.Stats = RunStats.Compute(bundle.Events);

        var lines = bundle.ToCsv().Trim().Split('\n').Select(l => l.Trim()).ToArray();
        Assert.Equal("turn,vad_ms,stt_ms,agent_ms,tts_ms,out_ms,total_ms,rtf", lines[0]);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("1,800.0,300.0,5.0,90.0,2.0,1197.0,", lines[1]);
        Assert.StartsWith("2,400.0,200.0,4.0,80.0,1.0,685.0,", lines[2]);
    }

    [Fact]
    public void Compare_Uses_The_Older_Run_As_Baseline_And_Warns_When_Contexts_Differ()
    {
        var older = new RunBundle
        {
            Number = 3,
            Label = "whispercpp·echo·piper",
            SourceDescription = "scripted · 2 utterance(s) · 1500 ms gaps",
            Context = new RunContext(8, "OS-A", "X64", ModelsCached: false),
            Events = Turn(0, 800, 300, 5, 90, 2).ToList(),
        };
        older.Stats = RunStats.Compute(older.Events);
        var newer = new RunBundle
        {
            Number = 7,
            Label = "whispercpp·echo·kokoro",
            SourceDescription = "mic",
            Context = new RunContext(16, "OS-A", "X64", ModelsCached: true),
            Events = Turn(0, 400, 200, 4, 80, 1).ToList(),
        };
        newer.Stats = RunStats.Compute(newer.Events);

        // Selection order must not matter.
        var compare = RunCompare.Build(newer, older);

        Assert.Equal(3, compare.Baseline.Number);
        Assert.Equal(7, compare.Current.Number);
        Assert.StartsWith("▼", compare.Headline); // 685 vs 1197 — faster
        Assert.Contains(compare.Rows, r => r.Label == "TTFB p50" && r.Improved);
        Assert.Contains(compare.Rows, r => r.Label == "VAD p50");
        Assert.Contains(compare.Warnings, w => w.Contains("8 vs 16 cores"));
        Assert.Contains(compare.Warnings, w => w.Contains("cold model cache"));
        Assert.Contains(compare.Warnings, w => w.Contains("Different inputs"));
    }

    [Fact]
    public void Store_Numbers_Runs_Scans_Newest_First_And_Skips_Corrupt_Files()
    {
        var dir = TestSupport.TempDir();
        var store = new RunStore(dir);

        var first = new RunBundle { Label = "a" };
        var second = new RunBundle { Label = "b" };
        store.Save(first);
        store.Save(second);
        Assert.Equal(1, first.Number);
        Assert.Equal(2, second.Number);

        // A truncated bundle must not break the folder scan.
        File.WriteAllText(Path.Combine(dir, "run-0003.json"), "{ not json");

        var all = store.LoadAll();
        Assert.Equal(2, all.Count);
        Assert.Equal("b", all[0].Label); // newest first

        store.Delete(second);
        Assert.Single(store.LoadAll());
    }
}
