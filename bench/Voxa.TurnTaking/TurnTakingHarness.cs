using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Voxa.AspNetCore;

namespace Voxa.TurnTaking;

/// <summary>
/// Orchestrates a VRT-001 run: build the (offline-by-default) services, walk the corpus, run each sample
/// through the composed pipeline, and write the per-sample JSON records. The testable entry point — the
/// smoke test calls <see cref="RunAsync"/> directly with the mini fixture.
/// </summary>
public sealed class TurnTakingHarness
{
    /// <summary>Per-sample wall-clock cap so a wedged sample can't hang the whole run.</summary>
    public static readonly TimeSpan PerSampleWallCap = TimeSpan.FromSeconds(30);

    public sealed record Options(
        string CorpusDir, string OutDir, string? Category, int? Limit, string Stt, string Tts, string Llm);

    public sealed record Result(IReadOnlyList<SampleRecord> Records, IReadOnlyList<string> Skipped);

    public static async Task<Result> RunAsync(Options options, Action<string>? log = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(options.OutDir);

        var skipped = new List<string>();
        var samples = CorpusWalker.Walk(options.CorpusDir, options.Category, options.Limit,
            s => { skipped.Add(s); log?.Invoke(s); });

        await using var root = BuildServiceProvider(options);
        var engines = new EngineNames(options.Stt, options.Llm, options.Tts);

        var records = new List<SampleRecord>();
        foreach (var sample in samples)
        {
            log?.Invoke($"running {sample.Category}/{sample.SampleId} …");
            records.Add(await SampleRunner.RunAsync(root, sample, options.OutDir, engines, PerSampleWallCap, ct)
                .ConfigureAwait(false));
        }
        return new Result(records, skipped);
    }

    private static ServiceProvider BuildServiceProvider(Options options)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Voxa:Stt"] = options.Stt,
            ["Voxa:Tts"] = options.Tts,
            ["Voxa:Agent:Provider"] = options.Llm,    // "Echo" by default — the deterministic mock LLM
            ["Voxa:Diagnostics:Enabled"] = "true",     // mandatory: the harness reads stage timings from the hub
            ["Voxa:Models:EagerWarmup"] = "false",     // no warm-up (we use a plain ServiceCollection anyway)
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        // A plain ServiceCollection has no implicit IConfiguration; the agent factory resolves it.
        services.AddSingleton<IConfiguration>(config);
        services.AddVoxa(config);                                       // meta: Echo agent + built-ins + Silero VAD
        services.AddVoxa(config, voxa => MockProviders.Register(voxa)); // merge in the Mock STT/TTS providers
        return services.BuildServiceProvider();
    }
}
