using Voxa.TurnTaking;

// Thin CLI: parse flags, then either (re)build the mini fixture or run the harness. All testable logic
// lives in the library types (TurnTakingHarness, CorpusWalker, …) so the smoke test calls them directly.
var flags = ParseFlags(args);

// Dev verb: regenerate the checked-in mini fixture from a real source WAV (e.g. apps/Voxa.Studio/fixtures/jfk.wav).
if (flags.TryGetValue("make-mini-fixture", out var sourceWav) && sourceWav is not null)
{
    var dest = flags.GetValueOrDefault("dest") ?? Path.Combine(AppContext.BaseDirectory, "fixtures", "fdb-mini");
    MiniFixtureBuilder.Build(sourceWav, dest);
    Console.WriteLine($"Mini fixture written to {dest}");
    return 0;
}

var corpusDir = flags.GetValueOrDefault("corpus-dir") ?? Path.Combine(AppContext.BaseDirectory, "fixtures", "fdb-mini");
var options = new TurnTakingHarness.Options(
    CorpusDir: corpusDir,
    OutDir:    flags.GetValueOrDefault("out-dir") ?? Path.Combine(Directory.GetCurrentDirectory(), "fdb-out"),
    Category:  flags.GetValueOrDefault("category"),
    Limit:     int.TryParse(flags.GetValueOrDefault("limit"), out var lim) ? lim : null,
    Stt:       flags.GetValueOrDefault("stt") ?? "mock",
    Tts:       flags.GetValueOrDefault("tts") ?? "mock",
    Llm:       flags.GetValueOrDefault("llm") ?? "Echo");

Console.WriteLine($"VRT-001 turn-taking harness — corpus: {corpusDir} (engines: stt={options.Stt} llm={options.Llm} tts={options.Tts})");
var result = await TurnTakingHarness.RunAsync(options, Console.WriteLine);
Console.WriteLine($"\n{result.Records.Count} sample(s) written to {options.OutDir}.");
var errors = result.Records.Count(r => r.Error is not null);
if (errors > 0) Console.WriteLine($"  {errors} sample(s) errored (see the records).");
return 0;

static Dictionary<string, string?> ParseFlags(string[] args)
{
    var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
        var key = args[i][2..];
        var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : null;
        map[key] = value;
    }
    return map;
}
