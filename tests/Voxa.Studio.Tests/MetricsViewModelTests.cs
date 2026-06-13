using Microsoft.Extensions.Configuration;
using Voxa.AspNetCore;
using Voxa.Diagnostics;
using Voxa.Speech;
using Voxa.Studio.Audio;
using Voxa.Studio.Services;
using Voxa.Studio.ViewModels;

namespace Voxa.Studio.Tests;

/// <summary>
/// VST-002 D4: the Metrics workbench VM — scripted runs end themselves and persist a bundle,
/// the live header is fed by real hub event shapes, compare needs exactly two checked runs,
/// and the config snapshot never carries a key.
/// </summary>
public class MetricsViewModelTests
{
    private static MetricsViewModel Vm(string? runsDir = null) =>
        new(TestSupport.Services()) { RunsDirOverride = runsDir ?? TestSupport.TempDir() };

    /// <summary>A real TalkSession over a trivial model-free chain — the D3 compose seam.</summary>
    private static TalkSession FakeSession(IServiceProvider provider, IStudioAudioDevice device) =>
        TalkSession.Create(provider, device,
            _ => new ComposedVoice([_ => new TranscriptionFilter()], 16000, 16000));

    private static RunEvent StageEvent(string stage, double ms) =>
        new() { Kind = "stage", Stage = stage, Ms = ms };

    private static RunBundle Bundle(string label, params double[] vadPerTurn)
    {
        var events = new List<RunEvent>();
        long t = 0;
        foreach (var vad in vadPerTurn)
        {
            events.Add(new RunEvent { Micros = t, Kind = "stage", Stage = "vad_close", Ms = vad });
            events.Add(new RunEvent { Micros = t + 1, Kind = "stage", Stage = "stt_final", Ms = 200 });
            events.Add(new RunEvent { Micros = t + 2, Kind = "stage", Stage = "audio_out", Ms = 1 });
            t += 1_000_000;
        }
        var bundle = new RunBundle { Label = label, Events = events };
        bundle.Stats = RunStats.Compute(events);
        return bundle;
    }

    [Fact]
    public void Source_Flags_Follow_The_Index()
    {
        var vm = Vm();
        Assert.True(vm.IsScriptedSource);
        vm.SourceIndex = 1;
        Assert.True(vm.IsWavSource);
        vm.SourceIndex = 2;
        Assert.True(vm.IsMicSource);
        Assert.False(vm.IsScriptedSource);
    }

    [Fact]
    public void The_Live_Header_Counts_Turns_Ttfb_And_Errors_From_Hub_Events()
    {
        var vm = Vm();
        vm.EnqueueForTest(new StageLatencyEvent("vad_close", 800));
        vm.EnqueueForTest(new StageLatencyEvent("stt_final", 300));
        vm.EnqueueForTest(new StageLatencyEvent("audio_out", 2));
        vm.EnqueueForTest(new PipelineErrorEvent("tap", "boom"));
        vm.DrainPending();

        Assert.Equal(1, vm.TurnCount);
        Assert.Equal("1102 ms", vm.LastTtfbText);
        Assert.Equal(1, vm.RunErrorCount);
    }

    [Fact]
    public async Task A_Scripted_Run_Ends_Itself_And_Persists_A_Bundle()
    {
        var dir = TestSupport.TempDir();
        var vm = Vm(dir);
        vm.SessionFactoryOverride = FakeSession;
        vm.UtteranceTimeoutMs = 150; // no bot in the trivial chain — the timeout advances the deck
        vm.ScriptGraceMs = 10;
        vm.GapMs = 500;
        vm.AddFixtureToScriptCommand.Execute(null);

        await vm.RunCommand.ExecuteAsync(null);
        Assert.True(vm.IsRunning);

        // The driver ends the run itself: condition-poll, never a fixed sleep.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (vm.IsRunning && DateTime.UtcNow < deadline) await Task.Delay(25);

        Assert.False(vm.IsRunning);
        Assert.Single(vm.Runs);
        Assert.NotNull(vm.SelectedRun);
        Assert.Single(Directory.GetFiles(dir, "run-*.json"));
        var bundle = vm.SelectedRun!.Bundle;
        Assert.Contains("scripted · 1 utterance(s)", bundle.SourceDescription);
        Assert.Equal(0, bundle.Stats.TurnCount); // the trivial chain produces no turns — honest
        Assert.Contains("No completed turns", vm.TakeawayText);
    }

    [Fact]
    public void Run_Refuses_An_Empty_Script_In_Words()
    {
        var vm = Vm();
        vm.RunCommand.Execute(null);
        Assert.False(vm.IsRunning);
        Assert.Contains("at least one utterance", vm.ErrorText);
    }

    [Fact]
    public void A_Live_Talk_Or_Builder_Session_Blocks_Run()
    {
        var vm = Vm();
        vm.AddFixtureToScriptCommand.Execute(null);
        Assert.True(vm.RunCommand.CanExecute(null));
        vm.RunBlocked = true;
        Assert.False(vm.RunCommand.CanExecute(null));
    }

    [Fact]
    public void A_Live_Metrics_Run_Blocks_Config_Apply()
    {
        // Regression: only Talk used to set ApplyBlocked, so a draft could be applied while a
        // run was recording — and the bundle then described the wrong config.
        var shell = new MainWindowViewModel(TestSupport.Services());
        Assert.False(shell.Config.ApplyBlocked);
        shell.Metrics.IsRunning = true;
        Assert.True(shell.Config.ApplyBlocked);
        shell.Metrics.IsRunning = false;
        Assert.False(shell.Config.ApplyBlocked);
    }

    [Fact]
    public async Task The_Bundle_Describes_The_Config_The_Run_Started_With()
    {
        // Regression: BuildBundle() read _services.Configuration at STOP time, so an Apply that
        // landed mid-run rewrote the saved label/profile/config — evidence describing a pipeline
        // that never produced those events. The identity is captured at run start.
        var services = TestSupport.Services();
        var vm = new MetricsViewModel(services) { RunsDirOverride = TestSupport.TempDir() };
        vm.SessionFactoryOverride = FakeSession;
        vm.UtteranceTimeoutMs = 150;
        vm.ScriptGraceMs = 10;
        vm.GapMs = 500;
        vm.AddFixtureToScriptCommand.Execute(null);

        await vm.RunCommand.ExecuteAsync(null);
        Assert.True(vm.IsRunning);

        // A config Apply lands while the run is recording (the shell normally blocks this for
        // Metrics now, but the bundle must stay truthful regardless of who wins that race).
        services.Reconfigure(new Dictionary<string, string?>
        {
            ["Voxa:Tts"] = "Kokoro",
            ["Voxa:Profile"] = "Quality",
        });

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (vm.IsRunning && DateTime.UtcNow < deadline) await Task.Delay(25);

        var bundle = vm.SelectedRun!.Bundle;
        Assert.Equal("whispercpp·echo·piper", bundle.Label); // what RAN, not what was applied
        Assert.Equal("Default", bundle.Profile);
        Assert.Equal("Piper", bundle.Config["Voxa:Tts"]);
    }

    [Fact]
    public void Checking_Exactly_Two_Runs_Builds_A_Compare()
    {
        var dir = TestSupport.TempDir();
        var store = new RunStore(dir);
        store.Save(Bundle("whispercpp·echo·piper", 800, 900));
        store.Save(Bundle("whispercpp·echo·kokoro", 400, 500));
        var vm = Vm(dir);

        Assert.Equal(2, vm.Runs.Count);
        Assert.Null(vm.Compare);

        vm.Runs[0].IsChecked = true;
        Assert.Null(vm.Compare); // one is not a comparison
        vm.Runs[1].IsChecked = true;
        Assert.NotNull(vm.Compare);
        Assert.Equal(1, vm.Compare!.Baseline.Number); // the older run is the baseline
        Assert.StartsWith("▼", vm.Compare.Headline);  // kokoro run is faster

        vm.Runs[0].IsChecked = false;
        Assert.Null(vm.Compare);
    }

    [Fact]
    public void Selecting_A_Run_Fills_The_Percentile_Card_And_Delta_Vs_The_Previous_Run()
    {
        var dir = TestSupport.TempDir();
        var store = new RunStore(dir);
        store.Save(Bundle("piper", 800));   // #1: ttfb 1001
        store.Save(Bundle("kokoro", 400));  // #2: ttfb 601
        var vm = Vm(dir);

        Assert.Equal(2, vm.SelectedRun!.Bundle.Number); // newest selected by default
        Assert.Equal("601", vm.P50Text);
        Assert.NotNull(vm.DeltaText);
        Assert.True(vm.DeltaImproved);
        Assert.Contains("vs run #1", vm.DeltaText);
        Assert.Single(vm.SelectedTurns);
        Assert.NotEmpty(vm.TakeawayText);
        Assert.Contains("1 turn(s)", vm.MetaText);
    }

    [Fact]
    public void FocusStage_Lands_On_A_Run_With_The_Stage_Highlighted()
    {
        var dir = TestSupport.TempDir();
        new RunStore(dir).Save(Bundle("piper", 800));
        var vm = Vm(dir);
        vm.SelectedRun = null;

        vm.FocusStage("stt_final"); // Talk's waterfall deep-link entry point
        Assert.Equal("stt_final", vm.FocusedStage);
        Assert.NotNull(vm.SelectedRun);
    }

    [Fact]
    public void The_Config_Snapshot_Never_Carries_An_Api_Key()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Voxa:Stt"] = "WhisperCpp",
            ["Voxa:Agent:Provider"] = "OpenAI",
            ["Voxa:Agent:ApiKey"] = "sk-super-secret",
            ["Voxa:OpenAI:ApiKey"] = "sk-other-secret",
        }).Build();

        var snapshot = MetricsViewModel.SnapshotConfig(config);

        Assert.Equal("WhisperCpp", snapshot["Voxa:Stt"]);
        Assert.Equal("OpenAI", snapshot["Voxa:Agent:Provider"]);
        Assert.DoesNotContain(snapshot.Keys, k => k.Contains("ApiKey", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Export_Writes_The_Per_Turn_Csv_Next_To_The_Bundles()
    {
        var dir = TestSupport.TempDir();
        new RunStore(dir).Save(Bundle("piper", 800, 400));
        var vm = Vm(dir);

        await vm.ExportCsvCommand.ExecuteAsync(null);

        var csv = File.ReadAllText(Path.Combine(dir, "run-0001.csv"));
        Assert.StartsWith("turn,vad_ms", csv);
        Assert.Equal(3, csv.Trim().Split('\n').Length);
    }
}

/// <summary>The scripted capture device: queued utterances stream as 20 ms frames, then silence.</summary>
public class ScriptedAudioDeviceTests
{
    [Fact]
    public async Task Capture_Streams_The_Utterance_Then_Silence_Padding_The_Tail()
    {
        var device = new ScriptedAudioDevice { Paced = false };
        const int rate = 16000, frameBytes = rate / 50 * 2; // 640

        // 1.5 frames of a recognizable pattern: the tail frame must be padded with silence.
        var utterance = new byte[frameBytes + frameBytes / 2];
        Array.Fill(utterance, (byte)0x42);
        device.EnqueueUtterance(utterance);

        var frames = new List<byte[]>();
        using var cts = new CancellationTokenSource();
        await foreach (var frame in device.CaptureAsync(device.CaptureEndpoints()[0], rate, cts.Token))
        {
            frames.Add(frame.ToArray());
            if (frames.Count == 4) cts.Cancel();
        }

        Assert.All(frames, f => Assert.Equal(frameBytes, f.Length));
        Assert.All(frames[0], b => Assert.Equal(0x42, b));
        Assert.Equal(0x42, frames[1][frameBytes / 2 - 1]); // pattern up to the tail…
        Assert.Equal(0, frames[1][frameBytes / 2]);        // …then silence padding
        Assert.All(frames[2], b => Assert.Equal(0, b));    // gap silence
        Assert.All(frames[3], b => Assert.Equal(0, b));
    }

    [Fact]
    public void The_Device_Advertises_Synthetic_Endpoints()
    {
        var device = new ScriptedAudioDevice();
        Assert.Single(device.CaptureEndpoints());
        Assert.Single(device.RenderEndpoints());
        Assert.True(device.CaptureEndpoints()[0].IsDefault);
    }
}
