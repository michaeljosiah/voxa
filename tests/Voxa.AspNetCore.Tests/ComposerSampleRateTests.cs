using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Voxa.Processors;
using Voxa.Speech;

namespace Voxa.AspNetCore.Tests;

/// <summary>
/// Regression tests: when a provider section overrides InputSampleRate / OutputSampleRate,
/// the session envelope and the VAD must use the overridden (effective) rate — the same one
/// the descriptor binds into the processor — not the descriptor default. Otherwise clients
/// capture/play at the advertised default rate while the processors run at the override,
/// producing distorted audio or failed transcription.
/// </summary>
public class ComposerSampleRateTests
{
    private static IConfiguration Config(params (string Key, string Value)[] pairs)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => (string?)p.Value))
            .Build();

    private static IServiceProvider EmptyServices()
        => new ServiceCollection().BuildServiceProvider();

    private static VoxaSttDescriptor Stt(int defaultRate = 16000) => new(
        Name: "FakeStt", ConfigSection: "FakeStt", PreferredInputSampleRate: defaultRate,
        Validate: _ => [],
        CreateProcessor: (_, _) => throw new NotSupportedException("test descriptor"));

    private static VoxaTtsDescriptor Tts(int defaultRate = 24000) => new(
        Name: "FakeTts", ConfigSection: "FakeTts", OutputSampleRate: defaultRate,
        Validate: _ => [],
        CreateProcessor: (_, _) => throw new NotSupportedException("test descriptor"));

    private static DefaultVoicePipelineComposer Composer(
        IConfiguration config,
        VoxaOptions options,
        VoxaVadDescriptor? vad = null)
    {
        var registry = new VoxaProviderRegistry();
        registry.Add(Stt());
        registry.Add(Tts());
        if (vad is not null) registry.Add(vad);

        return new DefaultVoicePipelineComposer(
            Options.Create(options),
            registry,
            new VoxaTuningResolver(NullLogger<VoxaTuningResolver>.Instance),
            config,
            NullLogger<DefaultVoicePipelineComposer>.Instance);
    }

    private static VoxaOptions FakeProviderOptions(string vadEngine = "None") => new()
    {
        Stt = "FakeStt",
        Tts = "FakeTts",
        Vad = new VoxaVadOptions { Engine = vadEngine },
    };

    // ── Descriptor-level effective rate resolution ──────────────────────────

    [Fact]
    public void Stt_Descriptor_Uses_Config_Override_Over_Default()
    {
        var root = Config(("Voxa:FakeStt:InputSampleRate", "8000")).GetSection("Voxa");
        Assert.Equal(8000, Stt(defaultRate: 16000).GetEffectiveInputSampleRate(root));
    }

    [Fact]
    public void Tts_Descriptor_Uses_Config_Override_Over_Default()
    {
        var root = Config(("Voxa:FakeTts:OutputSampleRate", "48000")).GetSection("Voxa");
        Assert.Equal(48000, Tts(defaultRate: 24000).GetEffectiveOutputSampleRate(root));
    }

    [Fact]
    public void Descriptors_Fall_Back_To_Defaults_Without_Override()
    {
        var root = Config().GetSection("Voxa");
        Assert.Equal(16000, Stt().GetEffectiveInputSampleRate(root));
        Assert.Equal(24000, Tts().GetEffectiveOutputSampleRate(root));
    }

    [Fact]
    public void Descriptor_Custom_Resolver_Wins_Over_Convention()
    {
        var root = Config(("Voxa:FakeStt:InputSampleRate", "8000")).GetSection("Voxa");
        var stt = Stt() with { ResolveInputSampleRate = _ => 44100 };
        Assert.Equal(44100, stt.GetEffectiveInputSampleRate(root));
    }

    // ── Composer: session envelope rates ────────────────────────────────────

    [Fact]
    public void Composed_Session_Rates_Reflect_Config_Overrides()
    {
        var composer = Composer(
            Config(("Voxa:FakeStt:InputSampleRate", "8000"),
                   ("Voxa:FakeTts:OutputSampleRate", "48000")),
            FakeProviderOptions());

        var composed = composer.Compose(EmptyServices());

        Assert.Equal(8000,  composed.InputSampleRate);
        Assert.Equal(48000, composed.OutputSampleRate);
    }

    [Fact]
    public void Composed_Session_Rates_Default_To_Descriptor_Rates()
    {
        var composed = Composer(Config(), FakeProviderOptions()).Compose(EmptyServices());

        Assert.Equal(16000, composed.InputSampleRate);
        Assert.Equal(24000, composed.OutputSampleRate);
    }

    // ── Composer: VAD receives the effective input rate ─────────────────────

    [Fact]
    public void Vad_Settings_Use_Effective_Input_Rate_Not_Descriptor_Default()
    {
        VoxaVadSettings? captured = null;
        var vad = new VoxaVadDescriptor(
            Name: "CapturingVad",
            CreateProcessor: (_, settings) =>
            {
                captured = settings;
                return new SilenceGateProcessor();
            });

        var composer = Composer(
            Config(("Voxa:FakeStt:InputSampleRate", "8000")),
            FakeProviderOptions(vadEngine: "CapturingVad"),
            vad);

        var services = EmptyServices();
        var composed = composer.Compose(services);
        // The VAD factory is deferred; invoke it the way the route handler would.
        _ = composed.Parts[0](services);

        Assert.NotNull(captured);
        Assert.Equal(8000, captured!.SampleRate);
    }
}
