using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Voxa.Frames;
using Voxa.Processors;
using Voxa.Speech;

namespace Voxa.AspNetCore.Tests;

/// <summary>
/// VDX-008 WS2: registering a keyed background <see cref="IAgentTurnDriver"/> under
/// <see cref="VoxaBackgroundAgentOptions.ServiceKey"/> inserts the <see cref="BackgroundAgentProcessor"/>
/// stage after the agent; unregistered, the composed shape is byte-identical to today.
/// </summary>
public class ComposerBackgroundAgentTests
{
    private static VoxaSttDescriptor Stt() => new(
        Name: "FakeStt", ConfigSection: "FakeStt", PreferredInputSampleRate: 16000,
        Validate: _ => [],
        CreateProcessor: (_, _) => throw new NotSupportedException("test descriptor"));

    private static VoxaTtsDescriptor Tts() => new(
        Name: "FakeTts", ConfigSection: "FakeTts", OutputSampleRate: 24000,
        Validate: _ => [],
        CreateProcessor: (_, _) => throw new NotSupportedException("test descriptor"));

    private static DefaultVoicePipelineComposer Composer(VoxaOptions? options = null)
    {
        var registry = new VoxaProviderRegistry();
        registry.Add(Stt());
        registry.Add(Tts());

        return new DefaultVoicePipelineComposer(
            Options.Create(options ?? TestOptions()),
            registry,
            new VoxaTuningResolver(NullLogger<VoxaTuningResolver>.Instance),
            new ConfigurationBuilder().Build(),
            NullLogger<DefaultVoicePipelineComposer>.Instance);
    }

    // Vad "None", diagnostics off: STT(0), TranscriptionFilter(1), agent(2), [background(3)],
    // SentenceAggregator, TTS.
    private const int AgentIndex = 2;
    private const int BackgroundIndex = 3;

    private static VoxaOptions TestOptions() => new()
    {
        Stt = "FakeStt",
        Tts = "FakeTts",
        Vad = new VoxaVadOptions { Engine = "None" },
        Diagnostics = new VoxaDiagnosticsOptions { Enabled = false },
    };

    private static ServiceProvider WithDrivers(bool background)
    {
        var services = new ServiceCollection();
        // Host driver satisfies the agent stage without an AIAgent (VDX-007 path).
        services.AddSingleton<IAgentTurnDriver>(new FakeTurnDriver());
        if (background)
            services.AddVoxaBackgroundAgent(_ => new FakeTurnDriver());
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Registered_Background_Driver_Adds_The_Background_Stage_After_The_Agent()
    {
        var sp = WithDrivers(background: true);

        var composed = Composer().Compose(sp);

        Assert.Equal(6, composed.Parts.Count);
        Assert.IsType<AgentLoopProcessor>(composed.Parts[AgentIndex](sp));
        Assert.IsType<BackgroundAgentProcessor>(composed.Parts[BackgroundIndex](sp));
    }

    [Fact]
    public void Without_A_Background_Driver_The_Shape_Is_Unchanged()
    {
        var sp = WithDrivers(background: false);

        var composed = Composer().Compose(sp);

        // Same part count as pre-VDX-008 (the STT/TTS factories are throwing fakes, so the shape
        // check stays at the factory level; the agent stage proves it's the loop, not background).
        Assert.Equal(5, composed.Parts.Count);
        Assert.IsType<AgentLoopProcessor>(composed.Parts[AgentIndex](sp));
    }

    [Fact]
    public void AddVoxaBackgroundAgent_Registers_A_Scoped_Keyed_Driver()
    {
        var services = new ServiceCollection();
        services.AddVoxaBackgroundAgent(_ => new FakeTurnDriver());
        using var sp = services.BuildServiceProvider();

        using var scope1 = sp.CreateScope();
        using var scope2 = sp.CreateScope();
        var a = scope1.ServiceProvider.GetKeyedService<IAgentTurnDriver>(VoxaBackgroundAgentOptions.ServiceKey);
        var b1 = scope2.ServiceProvider.GetKeyedService<IAgentTurnDriver>(VoxaBackgroundAgentOptions.ServiceKey);
        var b2 = scope2.ServiceProvider.GetKeyedService<IAgentTurnDriver>(VoxaBackgroundAgentOptions.ServiceKey);

        Assert.NotNull(a);
        Assert.NotNull(b1);
        Assert.NotSame(a, b1);  // per-connection isolation
        Assert.Same(b1, b2);    // stable within a connection
    }

    [Fact]
    public void Validator_Rejects_NonPositive_Background_Knobs()
    {
        var registry = new VoxaProviderRegistry();
        registry.Add(Stt());
        registry.Add(Tts());
        var validator = new VoxaOptionsValidator(registry, new ConfigurationBuilder().Build());

        var options = TestOptions();
        options.BackgroundAgent = new VoxaBackgroundAgentOptions
        {
            MaxConcurrentTasks = 0,
            MaxQueuedRequests = 0,
            TaskTimeoutSeconds = 0,
            MaxPendingResults = 0,
            HeldResultReleaseTimeoutMs = 0,
        };

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("MaxConcurrentTasks", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("MaxQueuedRequests", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("TaskTimeoutSeconds", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("MaxPendingResults", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("HeldResultReleaseTimeoutMs", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Default_Background_Options_Pass_Validation()
    {
        var registry = new VoxaProviderRegistry();
        registry.Add(Stt());
        registry.Add(Tts());
        var validator = new VoxaOptionsValidator(registry, new ConfigurationBuilder().Build());

        Assert.True(validator.Validate(null, TestOptions()).Succeeded);
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
}
