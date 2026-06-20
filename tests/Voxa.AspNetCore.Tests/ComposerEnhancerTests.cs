using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Voxa.Audio;
using Voxa.Speech;

namespace Voxa.AspNetCore.Tests;

/// <summary>
/// VLS-004 WS1: with <c>Voxa:Enhance:Engine</c> unset / "None" the composed pipeline is byte-identical to the
/// pre-VLS-004 chain (no enhancer stage). A registered engine inserts the <see cref="AudioEnhancerProcessor"/>
/// after the AEC stage and before the VAD. An unknown engine warns and runs without enhancement.
/// </summary>
public class ComposerEnhancerTests
{
    private static VoxaSttDescriptor Stt() => new(
        Name: "FakeStt", ConfigSection: "FakeStt", PreferredInputSampleRate: 16000,
        Validate: _ => [],
        CreateProcessor: (_, _) => throw new NotSupportedException("test descriptor"));

    private static VoxaTtsDescriptor Tts() => new(
        Name: "FakeTts", ConfigSection: "FakeTts", OutputSampleRate: 24000,
        Validate: _ => [],
        CreateProcessor: (_, _) => throw new NotSupportedException("test descriptor"));

    private static VoxaEnhancerDescriptor Enhancer() => new(
        Name: "FakeEnhancer",
        Validate: _ => [],
        CreateProcessor: (_, _) => new AudioEnhancerProcessor(new NullAudioEnhancer(16000)));

    private static VoxaAecDescriptor Aec() => new(
        Name: "FakeAec",
        CreateProcessor: (sp, _) => new EchoCancellerProcessor(sp.GetRequiredService<IEchoCanceller>()));

    private static DefaultVoicePipelineComposer Composer(
        VoxaOptions options,
        ILogger<DefaultVoicePipelineComposer>? logger = null,
        bool registerEnhancer = false,
        bool registerAec = false)
    {
        var registry = new VoxaProviderRegistry();
        registry.Add(Stt());
        registry.Add(Tts());
        if (registerEnhancer) registry.Add(Enhancer());
        if (registerAec) registry.Add(Aec());

        return new DefaultVoicePipelineComposer(
            Options.Create(options),
            registry,
            new VoxaTuningResolver(NullLogger<VoxaTuningResolver>.Instance),
            new ConfigurationBuilder().Build(),
            logger ?? NullLogger<DefaultVoicePipelineComposer>.Instance);
    }

    private static VoxaOptions OptionsWith(string enhanceEngine, string aecEngine = "None") => new()
    {
        Stt = "FakeStt",
        Tts = "FakeTts",
        Vad = new VoxaVadOptions { Engine = "None" }, // isolate the enhancer delta from the VAD block
        Aec = new VoxaAecOptions { Engine = aecEngine },
        Enhance = new VoxaEnhanceOptions { Engine = enhanceEngine },
        Diagnostics = new VoxaDiagnosticsOptions { Enabled = false },
    };

    private static IServiceProvider SessionServices()
        => new ServiceCollection()
            .AddScoped<IEchoCanceller>(_ => NullEchoCanceller.Instance)
            .BuildServiceProvider();

    [Fact]
    public void Enhance_None_Is_Byte_Identical_No_Stage()
    {
        var composed = Composer(OptionsWith("None")).Compose(SessionServices());
        Assert.Equal(5, composed.Parts.Count); // STT, filter, agent, aggregator, TTS
    }

    [Fact]
    public void Registered_Engine_Inserts_The_Enhancer_Before_The_VAD()
    {
        var composed = Composer(OptionsWith("FakeEnhancer"), registerEnhancer: true).Compose(SessionServices());

        Assert.Equal(6, composed.Parts.Count); // enhancer + the classic five
        Assert.IsType<AudioEnhancerProcessor>(composed.Parts[0](SessionServices())); // first stage (VAD is None)
    }

    [Fact]
    public void Engine_Name_Is_Case_Insensitive()
    {
        var composed = Composer(OptionsWith("fakeenhancer"), registerEnhancer: true).Compose(SessionServices());

        Assert.Equal(6, composed.Parts.Count);
        Assert.IsType<AudioEnhancerProcessor>(composed.Parts[0](SessionServices()));
    }

    [Fact]
    public void Enhancer_Runs_After_The_Aec_Stage()
    {
        // VLS-004: enhancer is inserted after the AEC stage (VRT-003) and before the VAD.
        var composed = Composer(OptionsWith("FakeEnhancer", aecEngine: "FakeAec"), registerEnhancer: true, registerAec: true)
            .Compose(SessionServices());

        var sp = SessionServices();
        Assert.IsType<EchoCancellerProcessor>(composed.Parts[0](sp));   // AEC first
        Assert.IsType<AudioEnhancerProcessor>(composed.Parts[1](sp));   // enhancer second
    }

    [Fact]
    public void Unknown_Engine_Warns_And_Runs_Without_Enhancement()
    {
        var logger = new ListLogger<DefaultVoicePipelineComposer>();
        var composed = Composer(OptionsWith("Nope"), logger).Compose(SessionServices());

        Assert.Equal(5, composed.Parts.Count); // no enhancer stage
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("Nope", StringComparison.Ordinal));
    }

    [Fact]
    public void Public_Builder_Registers_An_Enhancer_Descriptor_Through_AddVoxa()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddVoxa(config, b => b.AddProvider(new VoxaEnhancerDescriptor(
            "DeepFilterNet3", _ => [], (_, _) => new AudioEnhancerProcessor(new NullAudioEnhancer(16000)))));

        var registry = services.BuildServiceProvider().GetRequiredService<VoxaProviderRegistry>();

        Assert.True(registry.TryGetEnhancer("DeepFilterNet3", out _));
        Assert.Contains("DeepFilterNet3", registry.EnhancerNames, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
