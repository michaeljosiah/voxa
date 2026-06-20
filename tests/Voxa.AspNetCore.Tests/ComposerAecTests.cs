using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Voxa.Audio;
using Voxa.Speech;

namespace Voxa.AspNetCore.Tests;

/// <summary>
/// VRT-003 WS2: with <c>Voxa:Aec:Engine</c> unset / "None" the composed pipeline is byte-identical
/// to the pre-VRT-003 chain (no AEC stage, no far-end tap). A registered engine inserts the near-end
/// <see cref="EchoCancellerProcessor"/> before the VAD and the <see cref="EchoReferenceTapProcessor"/>
/// after TTS, both bound to the session's shared (scoped) <see cref="IEchoCanceller"/>. An unknown
/// engine name warns and runs without echo cancellation.
/// </summary>
public class ComposerAecTests
{
    private static VoxaSttDescriptor Stt() => new(
        Name: "FakeStt", ConfigSection: "FakeStt", PreferredInputSampleRate: 16000,
        Validate: _ => [],
        CreateProcessor: (_, _) => throw new NotSupportedException("test descriptor"));

    private static VoxaTtsDescriptor Tts() => new(
        Name: "FakeTts", ConfigSection: "FakeTts", OutputSampleRate: 24000,
        Validate: _ => [],
        CreateProcessor: (_, _) => throw new NotSupportedException("test descriptor"));

    // A stand-in AEC provider: its factory wires the session's scoped canceller into the near-end
    // processor, exactly as a real Voxa.Audio.Aec.* package would.
    private static VoxaAecDescriptor Aec() => new(
        Name: "FakeAec",
        CreateProcessor: (sp, _) => new EchoCancellerProcessor(sp.GetRequiredService<IEchoCanceller>()));

    private static DefaultVoicePipelineComposer Composer(
        VoxaOptions options,
        ILogger<DefaultVoicePipelineComposer>? logger = null,
        bool registerAec = false)
    {
        var registry = new VoxaProviderRegistry();
        registry.Add(Stt());
        registry.Add(Tts());
        if (registerAec) registry.Add(Aec());

        return new DefaultVoicePipelineComposer(
            Options.Create(options),
            registry,
            new VoxaTuningResolver(NullLogger<VoxaTuningResolver>.Instance),
            new ConfigurationBuilder().Build(),
            logger ?? NullLogger<DefaultVoicePipelineComposer>.Instance);
    }

    private static VoxaOptions OptionsWith(string aecEngine) => new()
    {
        Stt = "FakeStt",
        Tts = "FakeTts",
        Vad = new VoxaVadOptions { Engine = "None" }, // isolate the AEC delta from the VAD block
        Aec = new VoxaAecOptions { Engine = aecEngine },
        Diagnostics = new VoxaDiagnosticsOptions { Enabled = false },
    };

    // A session scope exposes the shared canceller, as the AEC provider package registers it (scoped).
    private static IServiceProvider SessionServices()
        => new ServiceCollection()
            .AddScoped<IEchoCanceller>(_ => NullEchoCanceller.Instance)
            .BuildServiceProvider();

    [Fact]
    public void Aec_None_Is_Byte_Identical_No_Stage_No_Tap()
    {
        var composed = Composer(OptionsWith("None")).Compose(SessionServices());

        // STT, TranscriptionFilter, agent, SentenceAggregator, TTS — and nothing else.
        Assert.Equal(5, composed.Parts.Count);
    }

    [Fact]
    public void Registered_Engine_Inserts_NearEnd_Before_And_Tap_After()
    {
        var composed = Composer(OptionsWith("FakeAec"), registerAec: true).Compose(SessionServices());

        // AEC, STT, filter, agent, aggregator, TTS, far-end tap.
        Assert.Equal(7, composed.Parts.Count);

        var sp = SessionServices();
        Assert.IsType<EchoCancellerProcessor>(composed.Parts[0](sp));        // near-end, before the VAD
        Assert.IsType<EchoReferenceTapProcessor>(composed.Parts[^1](sp));    // far-end tap, after TTS
    }

    [Fact]
    public void Engine_Name_Is_Case_Insensitive()
    {
        var composed = Composer(OptionsWith("fakeaec"), registerAec: true).Compose(SessionServices());

        Assert.Equal(7, composed.Parts.Count);
        Assert.IsType<EchoCancellerProcessor>(composed.Parts[0](SessionServices()));
    }

    [Fact]
    public void Unknown_Engine_Warns_And_Runs_Without_Aec()
    {
        var logger = new ListLogger<DefaultVoicePipelineComposer>();

        // Engine set but no descriptor registered → no stage, with a warning.
        var composed = Composer(OptionsWith("Nope"), logger).Compose(SessionServices());

        Assert.Equal(5, composed.Parts.Count); // no AEC stage, no tap
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("Nope", StringComparison.Ordinal));
    }

    [Fact]
    public void Public_Builder_Registers_An_Aec_Descriptor_Through_AddVoxa()
    {
        // VRT-003 (Codex P2): an external Voxa.Audio.Aec.* package must be able to register its descriptor
        // through the public VoxaBuilder, or Voxa:Aec:Engine would always be rejected as unregistered.
        var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddVoxa(config, b => b.AddProvider(new VoxaAecDescriptor(
            "WebRtc", (sp, _) => new EchoCancellerProcessor(NullEchoCanceller.Instance))));

        var registry = services.BuildServiceProvider().GetRequiredService<VoxaProviderRegistry>();

        Assert.True(registry.TryGetAec("WebRtc", out _));
        Assert.Contains("WebRtc", registry.AecNames, StringComparer.OrdinalIgnoreCase);
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
