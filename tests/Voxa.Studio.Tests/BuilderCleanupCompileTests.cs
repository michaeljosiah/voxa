using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Voxa;
using Voxa.AspNetCore;
using Voxa.Audio;
using Voxa.Speech;
using Voxa.Studio.Services;

namespace Voxa.Studio.Tests;

/// <summary>
/// The canvas-run compiler and C# exporter must apply the input cleanup the chain advertises (codex P2s):
/// AEC/denoise are read from the DRAWN graph (the Source node) — not the layered config — so the near-end
/// processors run before the VAD and the far-end tap after TTS (mirroring DefaultVoicePipelineComposer), a
/// graph with no cleanup never inherits a base-config engine, and a custom-shape C# export carries it too.
/// </summary>
public class BuilderCleanupCompileTests
{
    // A default-shape chain; cleanup rides the Source (mic) node, the way SeedFromPairs places it.
    private static IReadOnlyList<BuilderNode> Chain(string? aec = null, string? enhance = null)
    {
        var nodes = new List<BuilderNode>();
        BuilderNode Add(BuilderNodeKind kind, string? provider)
        {
            var node = new BuilderNode { Id = $"{kind}-{nodes.Count}", Kind = kind, Provider = provider };
            nodes.Add(node);
            return node;
        }
        var source = Add(BuilderNodeKind.Source, null);
        if (aec is not null) source.Options["AecEngine"] = aec;
        if (enhance is not null) source.Options["EnhanceEngine"] = enhance;
        Add(BuilderNodeKind.Vad, "Silero");
        Add(BuilderNodeKind.Stt, "WhisperCpp").Options["Model"] = "tiny.en";
        Add(BuilderNodeKind.Filter, null);
        Add(BuilderNodeKind.Agent, "Echo");
        Add(BuilderNodeKind.Aggregator, null);
        Add(BuilderNodeKind.Tts, "Piper").Options["Voice"] = "en_US-amy-low";
        Add(BuilderNodeKind.Sink, null);
        return nodes;
    }

    private static BuilderGraph GraphOf(IReadOnlyList<BuilderNode> chain)
    {
        var graph = new BuilderGraph();
        graph.Nodes.AddRange(chain);
        return graph;
    }

    private static ServiceProvider Provider(params (string Key, string? Value)[] extra)
    {
        var pairs = new Dictionary<string, string?>
        {
            ["Voxa:Stt"] = "WhisperCpp",
            ["Voxa:Tts"] = "Piper",
            ["Voxa:Agent:Provider"] = "Echo",
            ["Voxa:Vad:Engine"] = "Silero",
            ["Voxa:Models:CachePath"] = TestSupport.TempDir(),
        };
        foreach (var (k, v) in extra) pairs[k] = v;

        var config = new ConfigurationBuilder().AddInMemoryCollection(pairs).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddScoped<IEchoCanceller>(_ => NullEchoCanceller.Instance); // the far-end tap resolves this
        services.AddVoxa(config); // meta: the built-in WhisperCpp/Piper/Silero/Echo descriptors Compile needs
        services.AddVoxa(config, b => // core overload is idempotent — merge the fake cleanup engines in
        {
            b.AddProvider(new VoxaAecDescriptor("WebRtc",
                (sp, _) => new EchoCancellerProcessor(sp.GetRequiredService<IEchoCanceller>())));
            b.AddProvider(new VoxaEnhancerDescriptor("FakeEnh", _ => [],
                (_, settings, _) => new AudioEnhancerProcessor(new NullAudioEnhancer(settings.SampleRate))));
        });
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Compile_Inserts_Aec_Denoise_Before_The_Vad_And_The_Far_End_Tap()
    {
        using var provider = Provider();
        var compiled = BuilderChainCompiler.Compile(
            provider, provider.GetRequiredService<IConfiguration>(), Chain("WebRtc", "FakeEnh"));

        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;
        Assert.IsType<EchoCancellerProcessor>(compiled.Parts[0].Factory(sp));      // near-end AEC, before the VAD
        Assert.IsType<AudioEnhancerProcessor>(compiled.Parts[1].Factory(sp));      // denoise, after AEC
        Assert.IsType<EchoReferenceTapProcessor>(compiled.Parts[^1].Factory(sp));  // far-end tap, last part
    }

    [Fact]
    public void Compile_Without_Cleanup_Adds_No_Extra_Parts()
    {
        using var provider = Provider();
        var enabled = BuilderChainCompiler.Compile(
            provider, provider.GetRequiredService<IConfiguration>(), Chain("WebRtc", "FakeEnh"));
        var plain = BuilderChainCompiler.Compile(
            provider, provider.GetRequiredService<IConfiguration>(), Chain());

        // The default chain gains exactly three parts when cleanup is on: AEC + denoise + far-end tap.
        Assert.Equal(enabled.Parts.Count - 3, plain.Parts.Count);
    }

    [Fact] // codex round 3: a graph with no Source cleanup must follow the canvas, not inherit a base engine.
    public void Compile_Reads_Cleanup_From_The_Source_Node_Not_The_Base_Config()
    {
        using var baseHasAec = Provider(("Voxa:Aec:Engine", "WebRtc"), ("Voxa:Enhance:Engine", "FakeEnh"));
        using var noBase = Provider();

        var inherited = BuilderChainCompiler.Compile(
            baseHasAec, baseHasAec.GetRequiredService<IConfiguration>(), Chain()); // Source has no cleanup
        var baseline = BuilderChainCompiler.Compile(
            noBase, noBase.GetRequiredService<IConfiguration>(), Chain());

        Assert.Equal(baseline.Parts.Count, inherited.Parts.Count); // base-config cleanup did not leak into the run
    }

    [Fact] // codex round 3: a custom-shape C# export must carry the Source node's cleanup.
    public void GenerateCSharp_Emits_Source_Node_Cleanup()
    {
        var withCleanup = Chain("WebRtc", "FakeEnh");
        var code = BuilderChainCompiler.GenerateCSharp(GraphOf(withCleanup), withCleanup);

        Assert.Contains("TryGetAec(\"WebRtc\"", code);
        Assert.Contains("TryGetEnhancer(\"FakeEnh\"", code);
        Assert.Contains("EchoReferenceTapProcessor", code);   // far-end tap
        Assert.Contains("using Voxa.Audio;", code);           // for the tap + IEchoCanceller

        var plain = Chain();
        var plainCode = BuilderChainCompiler.GenerateCSharp(GraphOf(plain), plain);
        Assert.DoesNotContain("TryGetAec", plainCode);
        Assert.DoesNotContain("EchoReferenceTapProcessor", plainCode);
    }
}
