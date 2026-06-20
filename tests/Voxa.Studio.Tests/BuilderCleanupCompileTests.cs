using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Voxa;
using Voxa.AspNetCore;
using Voxa.Audio;
using Voxa.Speech;
using Voxa.Studio.Services;

namespace Voxa.Studio.Tests;

/// <summary>
/// The canvas-run compiler must apply the input cleanup it advertises (codex P2): when an AEC / denoise
/// engine is registered and selected, <see cref="BuilderChainCompiler.Compile"/> inserts the near-end AEC
/// + enhancer before the VAD and the far-end reference tap after TTS — mirroring DefaultVoicePipelineComposer
/// — so the live experiment matches the drawn chain. With none configured it's byte-identical to before.
/// </summary>
public class BuilderCleanupCompileTests
{
    private static IReadOnlyList<BuilderNode> DefaultChain()
    {
        var nodes = new List<BuilderNode>();
        BuilderNode Add(BuilderNodeKind kind, string? provider)
        {
            var node = new BuilderNode { Id = $"{kind}-{nodes.Count}", Kind = kind, Provider = provider };
            nodes.Add(node);
            return node;
        }
        Add(BuilderNodeKind.Source, null);
        Add(BuilderNodeKind.Vad, "Silero");
        Add(BuilderNodeKind.Stt, "WhisperCpp").Options["Model"] = "tiny.en";
        Add(BuilderNodeKind.Filter, null);
        Add(BuilderNodeKind.Agent, "Echo");
        Add(BuilderNodeKind.Aggregator, null);
        Add(BuilderNodeKind.Tts, "Piper").Options["Voice"] = "en_US-amy-low";
        Add(BuilderNodeKind.Sink, null);
        return nodes;
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
        using var provider = Provider(("Voxa:Aec:Engine", "WebRtc"), ("Voxa:Enhance:Engine", "FakeEnh"));
        var compiled = BuilderChainCompiler.Compile(
            provider, provider.GetRequiredService<IConfiguration>(), DefaultChain());

        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;
        Assert.IsType<EchoCancellerProcessor>(compiled.Parts[0].Factory(sp));      // near-end AEC, before the VAD
        Assert.IsType<AudioEnhancerProcessor>(compiled.Parts[1].Factory(sp));      // denoise, after AEC
        Assert.IsType<EchoReferenceTapProcessor>(compiled.Parts[^1].Factory(sp));  // far-end tap, last part
    }

    [Fact]
    public void Compile_Without_Cleanup_Adds_No_Extra_Parts()
    {
        using var with = Provider(("Voxa:Aec:Engine", "WebRtc"), ("Voxa:Enhance:Engine", "FakeEnh"));
        using var without = Provider();

        var enabled = BuilderChainCompiler.Compile(with, with.GetRequiredService<IConfiguration>(), DefaultChain());
        var plain = BuilderChainCompiler.Compile(without, without.GetRequiredService<IConfiguration>(), DefaultChain());

        // The default chain gains exactly three parts when cleanup is on: AEC + denoise + far-end tap.
        Assert.Equal(enabled.Parts.Count - 3, plain.Parts.Count);
    }
}
