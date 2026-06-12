using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Voxa.Diagnostics;
using Voxa.Processors;
using Voxa.Speech;

namespace Voxa.AspNetCore.Tests;

/// <summary>
/// VST-001 WS0-A4: with diagnostics off (the default), the composed pipeline is byte-identical
/// to the pre-diagnostics composition — no taps, no observer. With diagnostics on, the taps sit
/// after each stage and the VAD settings carry the hub observer.
/// </summary>
public class ComposerDiagnosticsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] pairs)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => (string?)p.Value))
            .Build();

    private static VoxaSttDescriptor Stt() => new(
        Name: "FakeStt", ConfigSection: "FakeStt", PreferredInputSampleRate: 16000,
        Validate: _ => [],
        CreateProcessor: (_, _) => throw new NotSupportedException("test descriptor"));

    private static VoxaTtsDescriptor Tts() => new(
        Name: "FakeTts", ConfigSection: "FakeTts", OutputSampleRate: 24000,
        Validate: _ => [],
        CreateProcessor: (_, _) => throw new NotSupportedException("test descriptor"));

    private static DefaultVoicePipelineComposer Composer(
        VoxaOptions options, VoxaVadDescriptor? vad = null)
    {
        var registry = new VoxaProviderRegistry();
        registry.Add(Stt());
        registry.Add(Tts());
        if (vad is not null) registry.Add(vad);

        return new DefaultVoicePipelineComposer(
            Options.Create(options),
            registry,
            new VoxaTuningResolver(NullLogger<VoxaTuningResolver>.Instance),
            Config(),
            NullLogger<DefaultVoicePipelineComposer>.Instance);
    }

    private static VoxaOptions OptionsWith(bool diagnostics, string vadEngine = "None") => new()
    {
        Stt = "FakeStt",
        Tts = "FakeTts",
        Vad = new VoxaVadOptions { Engine = vadEngine },
        Diagnostics = new VoxaDiagnosticsOptions { Enabled = diagnostics },
    };

    private static IServiceProvider ServicesWithHub()
        => new ServiceCollection().AddScoped(_ => new VoxaDiagnosticsHub()).BuildServiceProvider();

    // ── golden: diagnostics off composes exactly the classic chain ──────────

    [Fact]
    public void Diagnostics_Off_Composes_The_Classic_Five_Part_Chain()
    {
        var composed = Composer(OptionsWith(diagnostics: false)).Compose(ServicesWithHub());

        // STT, TranscriptionFilter, agent, SentenceAggregator, TTS — and nothing else.
        Assert.Equal(5, composed.Parts.Count);
    }

    [Fact]
    public void Diagnostics_On_Inserts_A_Tap_After_Each_Stage()
    {
        var composed = Composer(OptionsWith(diagnostics: true)).Compose(ServicesWithHub());

        // tapVad, STT, filter, tapStt, agent, tapAgent, aggregator, TTS, tapTts.
        Assert.Equal(9, composed.Parts.Count);

        var sp = ServicesWithHub();
        foreach (var tapIndex in new[] { 0, 3, 5, 8 })
            Assert.IsType<DiagnosticsTapProcessor>(composed.Parts[tapIndex](sp));
    }

    // ── VAD observer wiring ──────────────────────────────────────────────────

    private static VoxaVadDescriptor CapturingVad(Action<VoxaVadSettings> capture) => new(
        Name: "CapturingVad",
        CreateProcessor: (_, settings) =>
        {
            capture(settings);
            return new SilenceGateProcessor();
        });

    [Fact]
    public async Task Diagnostics_On_Wires_The_Vad_Probability_Observer_To_The_Hub()
    {
        VoxaVadSettings? captured = null;
        var composer = Composer(
            OptionsWith(diagnostics: true, vadEngine: "CapturingVad"),
            CapturingVad(s => captured = s));

        var sp = ServicesWithHub();
        var composed = composer.Compose(sp);
        _ = composed.Parts[0](sp); // the VAD factory

        Assert.NotNull(captured);
        Assert.NotNull(captured!.ProbabilityObserver);

        // The observer is a guarded publish: with a listener attached, a window lands as an event.
        var hub = sp.GetRequiredService<VoxaDiagnosticsHub>();
        var receivedOne = new TaskCompletionSource<DiagnosticEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            await foreach (var e in hub.SubscribeAsync(cts.Token))
            {
                receivedOne.TrySetResult(e);
                break;
            }
        });
        SpinWait.SpinUntil(() => hub.HasListeners, TimeSpan.FromSeconds(5));

        captured.ProbabilityObserver!(0.93f, 0.05, true, true);

        var received = await receivedOne.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var window = Assert.IsType<VadWindowEvent>(received);
        Assert.Equal(0.93f, window.Probability);
        Assert.True(window.Voiced);
        cts.Cancel();
    }

    [Fact]
    public void Diagnostics_Off_Leaves_The_Vad_Observer_Null()
    {
        VoxaVadSettings? captured = null;
        var composer = Composer(
            OptionsWith(diagnostics: false, vadEngine: "CapturingVad"),
            CapturingVad(s => captured = s));

        var sp = ServicesWithHub();
        _ = composer.Compose(sp).Parts[0](sp);

        Assert.NotNull(captured);
        Assert.Null(captured!.ProbabilityObserver);
    }
}
