using Voxa.TurnTaking;

// Thin CLI: parse flags, then either print help, (re)build the mini fixture, (re)write the baseline, or run
// the harness. All testable logic lives in the library types so the smoke test calls them directly.
var flags = ParseFlags(args);

if (flags.ContainsKey("help") || flags.ContainsKey("h"))
{
    PrintHelp();
    return 0;
}

// Dev verb: regenerate the checked-in mini fixture from a real source WAV (e.g. apps/Voxa.Studio/fixtures/jfk.wav).
if (flags.TryGetValue("make-mini-fixture", out var sourceWav) && sourceWav is not null)
{
    var dest = flags.GetValueOrDefault("dest") ?? Path.Combine(AppContext.BaseDirectory, "fixtures", "fdb-mini");
    MiniFixtureBuilder.Build(sourceWav, dest);
    Console.WriteLine($"Mini fixture written to {dest}");
    return 0;
}

var options = new TurnTakingHarness.Options(
    CorpusDir: flags.GetValueOrDefault("corpus-dir") ?? Path.Combine(AppContext.BaseDirectory, "fixtures", "fdb-mini"),
    OutDir:    flags.GetValueOrDefault("out-dir") ?? Path.Combine(Directory.GetCurrentDirectory(), "fdb-out"),
    Category:  flags.GetValueOrDefault("category"),
    Limit:     int.TryParse(flags.GetValueOrDefault("limit"), out var lim) ? lim : null,
    Stt:       flags.GetValueOrDefault("stt") ?? "mock",
    Tts:       flags.GetValueOrDefault("tts") ?? "mock",
    Llm:       flags.GetValueOrDefault("llm") ?? "Echo");

Console.WriteLine($"VRT-001 turn-taking harness — corpus: {options.CorpusDir} (engines: stt={options.Stt} llm={options.Llm} tts={options.Tts})");
var result = await TurnTakingHarness.RunAsync(options, Console.WriteLine);

Console.WriteLine($"\n{result.Records.Count} sample(s) → {options.OutDir} (summary.csv + score.json written).");
foreach (var s in result.Scores)
    Console.WriteLine(s.Skipped
        ? $"  {s.Category,-20} skipped"
        : s.Tor is double tor
            ? $"  {s.Category,-20} TOR {tor:0.###}"
            : $"  {s.Category,-20} ttfb_p50 {s.TtfbP50Ms:0.#} ms (responsiveness {s.Responsiveness:0.###})");

// Dev verb: write the scored run out as the checked-in baseline.
if (flags.TryGetValue("write-baseline", out var baselineOut) && baselineOut is not null)
{
    Baseline.FromScores(Path.GetFileName(options.CorpusDir), options.Stt == "mock" ? "mock" : "real", result.Scores).Save(baselineOut);
    Console.WriteLine($"Baseline written to {baselineOut}");
    return 0;
}

// Optional regression gate: diff the score against a checked-in baseline within tolerance.
if (flags.TryGetValue("baseline", out var baselinePath) && baselinePath is not null)
{
    var regressions = BaselineGate.Check(result.Scores, Baseline.Load(baselinePath));
    if (regressions.Count > 0)
    {
        Console.Error.WriteLine($"\nturn-taking regression(s) vs {baselinePath}:");
        foreach (var r in regressions) Console.Error.WriteLine($"  {r}");
        return 1;
    }
    Console.WriteLine($"\nno regression vs {baselinePath}.");
}

return result.Records.Any(r => r.Error is not null) ? 1 : 0;

static void PrintHelp()
{
    Console.WriteLine("""
        VRT-001 turn-taking quality benchmark (bench/Voxa.TurnTaking)

        Drives the real composed pipeline through a Full-Duplex-Bench corpus and scores the three
        cascade-fair categories (pause_handling / smooth_turn_taking / user_interruption);
        backchannel is discovered and skipped (N/A for a half-duplex cascade).

        Usage: dotnet run -c Release --project bench/Voxa.TurnTaking [-- <flags>]

          --corpus-dir <path>   FDB-layout corpus root (default: the bundled mini fixture)
          --out-dir <path>      where per-sample JSON, response WAVs, summary.csv, score.json land (default: ./fdb-out)
          --category <name>     restrict to one category
          --limit <n>           cap samples per category
          --stt / --tts / --llm provider names passed to AddVoxa (default: mock / mock / Echo — offline)
          --baseline <path>     diff the score against a checked-in baseline; non-zero exit on regression
          --make-mini-fixture <source.wav> [--dest <dir>]   regenerate the checked-in mini fixture
          --write-baseline <path>                           run, then write the score out as a baseline

        Full corpus (the real numbers): Full-Duplex-Bench v1.0 ships via Google Drive (see
        https://github.com/DanielLin94144/Full-Duplex-Bench). Fetch it, lay it out as
        <corpus>/<category>/<sample-id>/input.wav, and point --corpus-dir at it with real engines, e.g.
          --corpus-dir ./full-duplex-bench --stt WhisperCpp --tts Kokoro --llm openai
        """);
}

static Dictionary<string, string?> ParseFlags(string[] args)
{
    var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal) && !args[i].StartsWith('-')) continue;
        var key = args[i].TrimStart('-');
        var value = i + 1 < args.Length && !args[i + 1].StartsWith('-') ? args[++i] : null;
        map[key] = value;
    }
    return map;
}
