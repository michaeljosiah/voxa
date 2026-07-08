using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Voxa.Frames;
using Voxa.Processors;
using Voxa.Services.MicrosoftAgents;
using Voxa.Speech;

namespace Voxa.AspNetCore.Tests;

/// <summary>
/// VDX-007: a host-registered <see cref="IAgentTurnDriver"/> replaces the composer's Microsoft-Agents
/// stage under <c>UseDefaults()</c> — the host owns its own engine, memory, and tool round-trips, so it
/// no longer needs an <c>AIAgent</c>/<c>IChatClient</c> in DI (Ada's manual-pipeline motivation). With
/// no driver registered the agent stage resolves exactly as before.
/// </summary>
public class ComposerHostTurnDriverTests
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
        ILogger<DefaultVoicePipelineComposer>? logger = null)
    {
        var registry = new VoxaProviderRegistry();
        registry.Add(Stt());
        registry.Add(Tts());

        return new DefaultVoicePipelineComposer(
            Options.Create(TestOptions()),
            registry,
            new VoxaTuningResolver(NullLogger<VoxaTuningResolver>.Instance),
            new ConfigurationBuilder().Build(),
            logger ?? NullLogger<DefaultVoicePipelineComposer>.Instance);
    }

    // Vad "None" keeps the agent stage at a known index: STT(0), TranscriptionFilter(1),
    // agent(2), SentenceAggregator(3), TTS(4).
    private const int AgentIndex = 2;

    private static VoxaOptions TestOptions() => new()
    {
        Stt = "FakeStt",
        Tts = "FakeTts",
        Vad = new VoxaVadOptions { Engine = "None" },
        Diagnostics = new VoxaDiagnosticsOptions { Enabled = false },
    };

    [Fact]
    public void Registered_TurnDriver_Composes_Without_Any_Agent_In_DI()
    {
        // No AIAgent, no IChatClient, no IVoiceAgentFactory — only the host's driver.
        var services = new ServiceCollection();
        services.AddSingleton<IAgentTurnDriver>(new FakeTurnDriver());
        var sp = services.BuildServiceProvider();

        var stage = Composer().Compose(sp).Parts[AgentIndex](sp);

        Assert.IsType<AgentLoopProcessor>(stage);
    }

    [Fact]
    public void Without_A_TurnDriver_The_Agent_Requirement_Is_Unchanged()
    {
        var sp = new ServiceCollection().BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(
            () => Composer().Compose(sp).Parts[AgentIndex](sp));
        Assert.Contains("UseDefaults() needs an agent", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TurnDriver_Wins_Over_A_Registered_ChatClient()
    {
        // Both present: the host's driver takes the stage; the chat client is never wrapped.
        var services = new ServiceCollection();
        services.AddSingleton<IAgentTurnDriver>(new FakeTurnDriver());
        services.AddSingleton<Microsoft.Extensions.AI.IChatClient>(new ThrowingChatClient());
        var sp = services.BuildServiceProvider();

        var stage = Composer().Compose(sp).Parts[AgentIndex](sp);

        Assert.IsType<AgentLoopProcessor>(stage);
    }

    [Fact]
    public void TurnDriver_Plus_Configurator_Warns_And_Skips_The_Configurator()
    {
        var logger = new ListLogger<DefaultVoicePipelineComposer>();
        var configurator = new CapturingConfigurator();
        var services = new ServiceCollection();
        services.AddSingleton<IAgentTurnDriver>(new FakeTurnDriver());
        services.AddSingleton<IVoiceAgentConfigurator>(configurator);
        var sp = services.BuildServiceProvider();

        _ = Composer(logger).Compose(sp).Parts[AgentIndex](sp);

        Assert.False(configurator.Called);
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("IAgentTurnDriver", StringComparison.Ordinal) &&
            e.Message.Contains("IVoiceAgentConfigurator", StringComparison.Ordinal));
    }

    [Fact]
    public void Registering_A_TurnDriver_Does_Not_Change_The_Pipeline_Shape()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAgentTurnDriver>(new FakeTurnDriver());
        var sp = services.BuildServiceProvider();

        var composed = Composer().Compose(sp);

        Assert.Equal(5, composed.Parts.Count);
    }

    private sealed class FakeTurnDriver : IAgentTurnDriver
    {
        public async IAsyncEnumerable<Frame> RunTurnAsync(
            VoiceTurnContext ctx,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            yield return new LlmTextChunkFrame("ok");
            await Task.CompletedTask;
        }
    }

    private sealed class CapturingConfigurator : IVoiceAgentConfigurator
    {
        public bool Called { get; private set; }
        public void Configure(IServiceProvider connection, MicrosoftAgentVoiceOptions options) => Called = true;
    }

    private sealed class ThrowingChatClient : Microsoft.Extensions.AI.IChatClient
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
