using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Voxa.Processors;
using Voxa.Services.MicrosoftAgents;
using Voxa.Speech;

namespace Voxa.AspNetCore.Tests;

/// <summary>
/// VDX-006: a registered <see cref="IVoiceAgentConfigurator"/> owns the voice agent's
/// <see cref="MicrosoftAgentVoiceOptions"/> under <c>UseDefaults()</c> — the composer resolves it from
/// the per-connection scope, skips its built-in <c>InMemoryChatHistory</c>, and invokes the configurator
/// last. With none registered the agent stage is unchanged from pre-VDX-006 (built-in memory wired, no
/// configurator call); the built-in wiring is itself regression-covered by the existing route tests.
/// </summary>
public class ComposerAgentConfiguratorTests
{
    private static VoxaSttDescriptor Stt() => new(
        Name: "FakeStt", ConfigSection: "FakeStt", PreferredInputSampleRate: 16000,
        Validate: _ => [],
        CreateProcessor: (_, _) => throw new NotSupportedException("test descriptor"));

    private static VoxaTtsDescriptor Tts() => new(
        Name: "FakeTts", ConfigSection: "FakeTts", OutputSampleRate: 24000,
        Validate: _ => [],
        CreateProcessor: (_, _) => throw new NotSupportedException("test descriptor"));

    private static DefaultVoicePipelineComposer Composer(
        VoxaOptions options, ILogger<DefaultVoicePipelineComposer>? logger = null)
    {
        var registry = new VoxaProviderRegistry();
        registry.Add(Stt());
        registry.Add(Tts());

        return new DefaultVoicePipelineComposer(
            Options.Create(options),
            registry,
            new VoxaTuningResolver(NullLogger<VoxaTuningResolver>.Instance),
            new ConfigurationBuilder().Build(),
            logger ?? NullLogger<DefaultVoicePipelineComposer>.Instance);
    }

    // Vad "None" isolates the agent block and keeps it at a known index: STT(0), TranscriptionFilter(1),
    // agent(2), SentenceAggregator(3), TTS(4).
    private const int AgentIndex = 2;

    private static VoxaOptions OptionsWith(bool conversationMemory = true, int? maxResponseMs = null) => new()
    {
        Stt = "FakeStt",
        Tts = "FakeTts",
        Vad = new VoxaVadOptions { Engine = "None" },
        Diagnostics = new VoxaDiagnosticsOptions { Enabled = false },
        Agent = new VoxaAgentOptions
        {
            ConversationMemory = conversationMemory,
            MaxResponseDurationMs = maxResponseMs,
        },
    };

    // The composer resolves the agent (and the configurator) from the per-connection scope, so the
    // session SP carries the chat client that backs the agent plus any registered configurator.
    private static IServiceProvider SessionServices(IVoiceAgentConfigurator? configurator = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.AI.IChatClient>(new FakeChatClient());
        if (configurator is not null) services.AddSingleton(configurator);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Configurator_Is_Invoked_When_Registered()
    {
        var cfg = new CapturingConfigurator();
        var sp = SessionServices(cfg);

        _ = Composer(OptionsWith()).Compose(sp).Parts[AgentIndex](sp); // build the agent stage

        Assert.True(cfg.Called);
    }

    [Fact]
    public void Configurator_Present_Skips_BuiltIn_Memory()
    {
        var cfg = new CapturingConfigurator();
        var sp = SessionServices(cfg);

        _ = Composer(OptionsWith(conversationMemory: true)).Compose(sp).Parts[AgentIndex](sp);

        // The configurator runs after the composer's defaults; with built-in memory skipped, neither
        // BuildMessages nor OnTurnCompleted was set when the host took over.
        Assert.True(cfg.Called);
        Assert.False(cfg.BuildMessagesAlreadySet);
        Assert.False(cfg.OnTurnCompletedAlreadySet);
    }

    [Fact]
    public void Configurator_Runs_After_Composer_Applies_MaxResponseDuration()
    {
        var cfg = new CapturingConfigurator();
        var sp = SessionServices(cfg);

        _ = Composer(OptionsWith(maxResponseMs: 5000)).Compose(sp).Parts[AgentIndex](sp);

        // Host sees the composer's defaults (here MaxResponseDuration) and could override them.
        Assert.Equal(TimeSpan.FromSeconds(5), cfg.MaxResponseDurationSeen);
    }

    [Fact]
    public void ConversationMemory_With_Configurator_Logs_Precedence_At_Debug()
    {
        var logger = new ListLogger<DefaultVoicePipelineComposer>();
        var cfg = new CapturingConfigurator();
        var sp = SessionServices(cfg);

        _ = Composer(OptionsWith(conversationMemory: true), logger).Compose(sp).Parts[AgentIndex](sp);

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Debug &&
            e.Message.Contains("IVoiceAgentConfigurator", StringComparison.Ordinal));
    }

    [Fact]
    public void Registering_A_Configurator_Does_Not_Change_The_Pipeline_Shape()
    {
        // The seam is options-only: a configurator mutates the agent's MicrosoftAgentVoiceOptions, it never
        // adds or removes a stage. (Equivalence of the no-configurator *options* is guaranteed by the
        // unchanged built-in wiring and exercised by the end-to-end route tests; here we pin the structure.)
        var withoutSp = SessionServices(configurator: null);
        var withSp = SessionServices(new CapturingConfigurator());

        var without = Composer(OptionsWith()).Compose(withoutSp);
        var with = Composer(OptionsWith()).Compose(withSp);

        Assert.Equal(5, without.Parts.Count);
        Assert.Equal(without.Parts.Count, with.Parts.Count);
        Assert.IsType<AgentLoopProcessor>(without.Parts[AgentIndex](withoutSp));
        Assert.IsType<AgentLoopProcessor>(with.Parts[AgentIndex](withSp));
    }

    [Fact]
    public void AddVoxaVoiceAgentConfigurator_Forwards_To_The_Delegate()
    {
        MicrosoftAgentVoiceOptions? seen = null;
        var services = new ServiceCollection();
        services.AddVoxaVoiceAgentConfigurator((_, opts) => seen = opts);

        var resolved = services.BuildServiceProvider().GetRequiredService<IVoiceAgentConfigurator>();
        var probe = new MicrosoftAgentVoiceOptions();
        resolved.Configure(new ServiceCollection().BuildServiceProvider(), probe);

        Assert.Same(probe, seen);
    }

    private sealed class CapturingConfigurator : IVoiceAgentConfigurator
    {
        public bool Called { get; private set; }
        public bool BuildMessagesAlreadySet { get; private set; }
        public bool OnTurnCompletedAlreadySet { get; private set; }
        public TimeSpan? MaxResponseDurationSeen { get; private set; }

        public void Configure(IServiceProvider connection, MicrosoftAgentVoiceOptions options)
        {
            Called = true;
            BuildMessagesAlreadySet = options.BuildMessages is not null;
            OnTurnCompletedAlreadySet = options.OnTurnCompleted is not null;
            MaxResponseDurationSeen = options.MaxResponseDuration;
        }
    }

    private sealed class FakeChatClient : Microsoft.Extensions.AI.IChatClient
    {
        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
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
