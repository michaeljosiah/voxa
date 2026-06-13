using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Voxa.Studio.Services;

/// <summary>
/// One recorded diagnostic event, compacted for the run bundle (VST-002 §9.3). Only the fields
/// for the event's kind are set; JSON ignores the nulls. VAD windows (~31/s) and agent text
/// deltas are deliberately NOT recorded — their signal for the workbench is already captured by
/// the derived stage latencies, and dropping them keeps a ten-minute bundle in the tens of KB.
/// </summary>
public sealed record RunEvent
{
    public long Micros { get; init; }
    public string Kind { get; init; } = "";   // turn | transcript | stage | tts | error

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Edge { get; init; }        // turn: UserStarted…Interrupted
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }        // transcript (finals only) / error message
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Stage { get; init; }       // stage: vad_close … audio_out
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Ms { get; init; }          // stage
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Bytes { get; init; }          // tts chunk
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SampleRate { get; init; }     // tts chunk
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; init; }      // error source
}

/// <summary>
/// The machine context a run happened in (the R4 mitigation): comparisons across different
/// hardware or cold caches imply trends the numbers can't support, so the compare view warns
/// when contexts differ.
/// </summary>
public sealed record RunContext(int CoreCount, string Os, string ProcessArch, bool ModelsCached)
{
    public static RunContext Capture(bool modelsCached) => new(
        Environment.ProcessorCount,
        RuntimeInformation.OSDescription,
        RuntimeInformation.ProcessArchitecture.ToString(),
        modelsCached);
}

/// <summary>One completed turn's measured stages (the unit of every workbench chart).</summary>
public sealed record RunTurn(int Number, Dictionary<string, double> Stages, double TotalMs, double? Rtf);

/// <summary>Computed statistics stored in (and recomputable from) a bundle's event stream.</summary>
public sealed class RunStats
{
    public int TurnCount { get; init; }
    public int ErrorCount { get; init; }
    public int InterruptionCount { get; init; }
    public double TtfbP50 { get; init; }
    public double TtfbP95 { get; init; }
    public double TtfbMax { get; init; }
    public Dictionary<string, double> StageP50 { get; init; } = new();
    public double? TtsRtfMean { get; init; }
    public List<RunTurn> Turns { get; init; } = new();

    /// <summary>The five stages in pipeline order — chart and CSV column order everywhere.</summary>
    public static readonly string[] StageOrder = ["vad_close", "stt_final", "agent_first_token", "tts_first_byte", "audio_out"];

    /// <summary>
    /// Walk the event stream and assemble turns the same way the Talk view does: stage events
    /// accumulate, <c>audio_out</c> closes the turn (it is the last stage the sink publishes).
    /// </summary>
    public static RunStats Compute(IReadOnlyList<RunEvent> events)
    {
        var turns = new List<RunTurn>();
        var stages = new Dictionary<string, double>();
        int errors = 0, interruptions = 0;
        double chunkAudioSeconds = 0;
        long firstChunkMicros = 0, lastChunkMicros = 0;
        int chunkCount = 0;

        foreach (var e in events)
        {
            switch (e.Kind)
            {
                case "error":
                    errors++;
                    break;
                case "turn" when e.Edge == "Interrupted":
                    interruptions++;
                    break;
                case "tts" when e is { Bytes: > 0, SampleRate: > 0 }:
                    chunkAudioSeconds += e.Bytes.Value / (2.0 * e.SampleRate.Value); // PCM16 mono
                    if (chunkCount == 0) firstChunkMicros = e.Micros;
                    lastChunkMicros = e.Micros;
                    chunkCount++;
                    break;
                case "stage" when e is { Stage: not null, Ms: not null }:
                    stages[e.Stage] = e.Ms.Value;
                    if (e.Stage == "audio_out")
                    {
                        turns.Add(new RunTurn(
                            turns.Count + 1,
                            new Dictionary<string, double>(stages),
                            stages.Values.Sum(),
                            TurnRtf(chunkCount, firstChunkMicros, lastChunkMicros, chunkAudioSeconds, stages)));
                        stages.Clear();
                        chunkAudioSeconds = 0;
                        chunkCount = 0;
                    }
                    break;
            }
        }

        var totals = turns.Select(t => t.TotalMs).ToList();
        var rtfs = turns.Where(t => t.Rtf is not null).Select(t => t.Rtf!.Value).ToList();
        return new RunStats
        {
            TurnCount = turns.Count,
            ErrorCount = errors,
            InterruptionCount = interruptions,
            TtfbP50 = totals.Count > 0 ? Percentile(totals, 0.50) : 0,
            TtfbP95 = totals.Count > 0 ? Percentile(totals, 0.95) : 0,
            TtfbMax = totals.Count > 0 ? totals.Max() : 0,
            StageP50 = StageOrder
                .Select(s => (s, vals: turns.Where(t => t.Stages.ContainsKey(s)).Select(t => t.Stages[s]).ToList()))
                .Where(p => p.vals.Count > 0)
                .ToDictionary(p => p.s, p => Percentile(p.vals, 0.50)),
            TtsRtfMean = rtfs.Count > 0 ? rtfs.Average() : null,
            Turns = turns,
        };
    }

    /// <summary>
    /// Chunk-span RTF: wall time from first to last synthesized chunk over the audio duration
    /// produced. A single-chunk reply has no span, so it falls back to the measured
    /// <c>tts_first_byte</c> latency — the time it took to produce that one chunk.
    /// </summary>
    private static double? TurnRtf(
        int chunkCount, long firstMicros, long lastMicros, double audioSeconds, Dictionary<string, double> stages)
    {
        if (chunkCount == 0 || audioSeconds <= 0) return null;
        var wallSeconds = chunkCount > 1
            ? (lastMicros - firstMicros) / 1_000_000.0
            : stages.GetValueOrDefault("tts_first_byte") / 1000.0;
        return wallSeconds > 0 ? wallSeconds / audioSeconds : null;
    }

    /// <summary>Nearest-rank percentile — no interpolation, honest for small N (the D2 rule).</summary>
    public static double Percentile(IReadOnlyList<double> values, double p)
    {
        var sorted = values.Order().ToArray();
        var rank = (int)Math.Ceiling(p * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }
}

/// <summary>
/// A run: one configuration exercised by one input source for some duration (VST-002 §9). The
/// unit of record in the workbench; serialized as a JSON bundle under the user profile. Config
/// pairs are stored WITHOUT secrets — bundles are made to be shared.
/// </summary>
public sealed class RunBundle
{
    public int Schema { get; init; } = 1;
    public int Number { get; set; }
    public string Label { get; set; } = "";                 // "whispercpp·echo·piper"
    public DateTimeOffset StartedAt { get; set; }
    public double DurationSeconds { get; set; }
    public string SourceDescription { get; set; } = "";     // "mic" | "wav · jfk.wav" | "scripted · 8 utterances · 1500 ms gaps"
    public string Profile { get; set; } = "Default";
    public Dictionary<string, string?> Config { get; set; } = new();
    public RunContext Context { get; set; } = RunContext.Capture(modelsCached: false);
    public List<RunEvent> Events { get; set; } = new();
    public RunStats Stats { get; set; } = new();

    /// <summary>
    /// The one generated sentence naming the dominant stage and the obvious lever (§9.2's
    /// takeaway line). Rule-based, not an LLM — it is the tuner persona's "so what", and every
    /// lever it names is a real knob.
    /// </summary>
    public string Takeaway()
    {
        if (Stats.TurnCount == 0)
            return Stats.ErrorCount > 0
                ? $"No completed turns — {Stats.ErrorCount} pipeline error(s) recorded; fix those first."
                : "No completed turns — nothing to measure yet.";

        var p50 = Stats.StageP50;
        if (p50.Count == 0) return "Turns completed but no stage timings landed — check diagnostics.";
        var total = p50.Values.Sum();
        var (stage, ms) = p50.MaxBy(kv => kv.Value);
        var share = total > 0 ? (int)Math.Round(ms / total * 100) : 0;

        var sentence = stage switch
        {
            "vad_close" => $"VAD hangover is {share}% of p50 — lower Voxa:Vad:StopDurationMs or pick the LowLatency profile.",
            "stt_final" => $"STT decode is {share}% of p50 — a smaller Whisper model is the lever.",
            "agent_first_token" => $"Agent first-token is {share}% of p50 — a faster model (or shorter prompt) is the lever.",
            "tts_first_byte" => $"TTS first-byte is {share}% of p50 — a lower-RTF voice or eager aggregation (EagerFirstChunkMinChars) is the lever.",
            "audio_out" => $"Device enqueue is {share}% of p50 — check the output device's shared-mode buffering.",
            _ => $"{stage} dominates at {share}% of p50.",
        };
        if (Stats.ErrorCount > 0) sentence += $" ({Stats.ErrorCount} pipeline error(s) recorded.)";
        return sentence;
    }

    // ── persistence ──────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static RunBundle FromJson(string json) =>
        JsonSerializer.Deserialize<RunBundle>(json, JsonOptions)
        ?? throw new InvalidOperationException("Empty run bundle.");

    /// <summary>Per-turn stage CSV — the exportable evidence (§9.2). Columns in stage order.</summary>
    public string ToCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine("turn,vad_ms,stt_ms,agent_ms,tts_ms,out_ms,total_ms,rtf");
        foreach (var t in Stats.Turns)
        {
            sb.Append(t.Number.ToString(CultureInfo.InvariantCulture));
            foreach (var stage in RunStats.StageOrder)
                sb.Append(',').Append(t.Stages.GetValueOrDefault(stage).ToString("F1", CultureInfo.InvariantCulture));
            sb.Append(',').Append(t.TotalMs.ToString("F1", CultureInfo.InvariantCulture));
            sb.Append(',').Append(t.Rtf?.ToString("F3", CultureInfo.InvariantCulture) ?? "");
            sb.AppendLine();
        }
        return sb.ToString();
    }
}

/// <summary>One line of a run-vs-baseline comparison (per stage or headline metric).</summary>
public sealed record CompareRow(string Label, double BaselineMs, double CurrentMs)
{
    public double DeltaMs => CurrentMs - BaselineMs;
    public double DeltaPct => BaselineMs > 0 ? DeltaMs / BaselineMs * 100 : 0;
    public bool Improved => DeltaMs < 0;
    public string DeltaText => BaselineMs <= 0 ? "—"
        : $"{(Improved ? "▼" : "▲")} {Math.Abs(DeltaPct):F0}%  ({(Improved ? "−" : "+")}{Math.Abs(DeltaMs):F0} ms)";
}

/// <summary>
/// Two runs side by side (§9.2 compare). The baseline is the OLDER run — deltas read as "what
/// changed since". Context warnings are the R4 mitigation: different machines or cold caches
/// make latency deltas lie, so differences are named, not hidden.
/// </summary>
public sealed class RunCompare
{
    public required RunBundle Baseline { get; init; }
    public required RunBundle Current { get; init; }
    public required IReadOnlyList<CompareRow> Rows { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }

    public string Headline
    {
        get
        {
            var b = Baseline.Stats.TtfbP50;
            var c = Current.Stats.TtfbP50;
            if (b <= 0 || c <= 0) return "Not enough turns to compare.";
            var pct = (c - b) / b * 100;
            return pct <= 0
                ? $"▼ {Math.Abs(pct):F0}% p50 vs run #{Baseline.Number} ({Baseline.Label})"
                : $"▲ {pct:F0}% p50 vs run #{Baseline.Number} ({Baseline.Label})";
        }
    }

    public static RunCompare Build(RunBundle a, RunBundle b)
    {
        // Baseline = the older run, regardless of selection order.
        var (baseline, current) = a.Number <= b.Number ? (a, b) : (b, a);

        var rows = new List<CompareRow>
        {
            new("TTFB p50", baseline.Stats.TtfbP50, current.Stats.TtfbP50),
            new("TTFB p95", baseline.Stats.TtfbP95, current.Stats.TtfbP95),
        };
        foreach (var stage in RunStats.StageOrder)
        {
            var bs = baseline.Stats.StageP50.GetValueOrDefault(stage);
            var cs = current.Stats.StageP50.GetValueOrDefault(stage);
            if (bs > 0 || cs > 0) rows.Add(new CompareRow($"{StageLabel(stage)} p50", bs, cs));
        }

        var warnings = new List<string>();
        if (baseline.Context.CoreCount != current.Context.CoreCount)
            warnings.Add($"Different machines: {baseline.Context.CoreCount} vs {current.Context.CoreCount} cores.");
        if (baseline.Context.Os != current.Context.Os)
            warnings.Add("Different OS builds — absolute latencies may not be comparable.");
        if (baseline.Context.ModelsCached != current.Context.ModelsCached)
            warnings.Add("One run started with a cold model cache — first-run load time skews its early turns.");
        if (baseline.SourceDescription != current.SourceDescription)
            warnings.Add($"Different inputs ({baseline.SourceDescription} vs {current.SourceDescription}) — prefer the same scripted deck for honest deltas.");

        return new RunCompare { Baseline = baseline, Current = current, Rows = rows, Warnings = warnings };
    }

    private static string StageLabel(string stage) => stage switch
    {
        "vad_close" => "VAD",
        "stt_final" => "STT",
        "agent_first_token" => "AGENT",
        "tts_first_byte" => "TTS",
        "audio_out" => "OUT",
        _ => stage,
    };
}

/// <summary>
/// The on-disk run store: a folder of JSON bundles under the user profile; the run list is a
/// folder scan (§9.3). Nothing leaves the machine (P4).
/// </summary>
public sealed class RunStore
{
    private readonly string _dir;

    public RunStore(string? dir = null) =>
        _dir = dir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "voxa-runs");

    public string Directory => _dir;

    /// <summary>Assigns the next run number and writes the bundle. Returns the file path.</summary>
    public string Save(RunBundle bundle)
    {
        System.IO.Directory.CreateDirectory(_dir);
        bundle.Number = NextNumber();
        var path = Path.Combine(_dir, $"run-{bundle.Number:D4}.json");
        File.WriteAllText(path, bundle.ToJson());
        return path;
    }

    /// <summary>All bundles, newest first. Unreadable files are skipped, not fatal.</summary>
    public IReadOnlyList<RunBundle> LoadAll()
    {
        if (!System.IO.Directory.Exists(_dir)) return [];
        var bundles = new List<RunBundle>();
        foreach (var file in System.IO.Directory.EnumerateFiles(_dir, "run-*.json"))
        {
            try { bundles.Add(RunBundle.FromJson(File.ReadAllText(file))); }
            catch { /* a hand-edited or truncated bundle must not break the list */ }
        }
        return bundles.OrderByDescending(b => b.Number).ToList();
    }

    public void Delete(RunBundle bundle)
    {
        var path = Path.Combine(_dir, $"run-{bundle.Number:D4}.json");
        if (File.Exists(path)) File.Delete(path);
    }

    private int NextNumber()
    {
        var max = 0;
        foreach (var file in System.IO.Directory.EnumerateFiles(_dir, "run-*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (int.TryParse(name.AsSpan(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                max = Math.Max(max, n);
        }
        return max + 1;
    }
}
