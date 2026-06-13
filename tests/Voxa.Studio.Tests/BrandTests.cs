using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Voxa.Studio.Controls;
using Voxa.Studio.Services;
using Voxa.Studio.Theme;
using Voxa.Studio.ViewModels;
using Voxa.Studio.Views;

namespace Voxa.Studio.Tests;

/// <summary>
/// VST-002 D1: the brand layer — staged boot, the mark, the splash — verified headless.
/// Reduced-motion is forced in UI tests so rendering is deterministic (end-state, no timers).
/// </summary>
public class BrandTests
{
    [Fact]
    public async Task StartupCoordinator_Reports_The_Five_Stages_In_Order_And_Boots_Keyless()
    {
        var stages = new List<string>();
        var prior = Environment.GetEnvironmentVariable("VOXA_MODEL_CACHE");
        Environment.SetEnvironmentVariable("VOXA_MODEL_CACHE", null);
        try
        {
            var services = StartupCoordinator.Run(
                stages.Add,
                TestSupport.LocalConfig(),
                new Voxa.Studio.Audio.NullAudioDevice());

            // The splash microcopy ticks exactly these, in this order — honest progress.
            Assert.Equal(StartupCoordinator.Stages, stages);
            Assert.Contains("WhisperCpp", services.Registry.SttNames);
            await services.DisposeAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("VOXA_MODEL_CACHE", prior);
        }
    }

    [AvaloniaFact]
    public void VoxaMark_Renders_Statically_Under_Reduced_Motion()
    {
        MotionSettings.SetOverride(true);
        try
        {
            var window = new Window
            {
                Width = 100, Height = 100,
                Content = new VoxaMarkControl { Width = 88, Height = 88, Animated = true, Glow = true },
            };
            window.Show();

            // Reduced motion = finished mark immediately, no ticker — a frame must render.
            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            window.Close();
        }
        finally
        {
            MotionSettings.SetOverride(null);
        }
    }

    [AvaloniaFact]
    public void Mark_Glow_Toggling_Live_Drives_The_Pulse_After_Attach()
    {
        // Regression (PR #5 review): the titlebar mark binds Glow to IsLive, which is false at
        // attach. Toggling it true when a Talk session goes live must start the data-backed
        // pulse — the original control decided the ticker only at attach and never registered
        // AffectsRender, so the glow silently never appeared. Force animation on so the test is
        // deterministic regardless of the host's reduced-motion setting.
        MotionSettings.SetOverride(false);
        try
        {
            var mark = new VoxaMarkControl { Width = 18, Height = 18 }; // titlebar size; Animated defaults false
            var window = new Window { Width = 40, Height = 40, Content = mark };
            window.Show();

            Assert.False(mark.IsTickerRunning); // idle: no intro, session not live

            mark.Glow = true;               // a Talk session goes live
            Assert.True(mark.IsTickerRunning);  // the pulse must now run (the bug: it stayed idle)

            mark.Glow = false;              // session ends
            Assert.False(mark.IsTickerRunning); // and the pulse stops — no idle loop left running

            window.Close();
        }
        finally
        {
            MotionSettings.SetOverride(null);
        }
    }

    [AvaloniaFact]
    public void Mark_Glow_Toggle_Is_Static_Under_Reduced_Motion()
    {
        MotionSettings.SetOverride(true);
        try
        {
            var mark = new VoxaMarkControl { Width = 18, Height = 18 };
            var window = new Window { Width = 40, Height = 40, Content = mark };
            window.Show();

            mark.Glow = true;               // live — but reduced motion means a constant glow, no ticker
            Assert.False(mark.IsTickerRunning);

            window.Close();
        }
        finally
        {
            MotionSettings.SetOverride(null);
        }
    }

    [AvaloniaFact]
    public void Splash_Ticks_Real_Stages_And_Honest_Progress()
    {
        MotionSettings.SetOverride(true);
        try
        {
            var splash = new SplashWindow();
            splash.Show();

            foreach (var stage in StartupCoordinator.Stages)
                splash.ReportStage(stage);

            Assert.Equal("ready", splash.StageText.Text);
            Assert.Equal(splash.Width, splash.ProgressBar.Width); // full bar only at the real end
            splash.Close();
        }
        finally
        {
            MotionSettings.SetOverride(null);
        }
    }

    [AvaloniaFact]
    public void Splash_Surfaces_Boot_Failure_Instead_Of_Hanging()
    {
        MotionSettings.SetOverride(true);
        try
        {
            var splash = new SplashWindow();
            splash.Show();
            splash.ShowError("Voxa:Stt 'Nope' is not a registered provider.");

            Assert.Contains("not a registered provider", splash.StageText.Text);
            splash.Close();
        }
        finally
        {
            MotionSettings.SetOverride(null);
        }
    }

    /// <summary>
    /// Not an assertion — a capture utility: VOXA_STUDIO_CAPTURE=1 renders the splash and the
    /// shell to PNGs (path printed) so a human can eyeball the brand work without launching
    /// the app. Skipped (trivially green) otherwise.
    /// </summary>
    [AvaloniaFact]
    public void Capture_Brand_Screenshots_When_Requested()
    {
        if (Environment.GetEnvironmentVariable("VOXA_STUDIO_CAPTURE") != "1") return;

        MotionSettings.SetOverride(true);
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "voxa-studio-captures");
            Directory.CreateDirectory(dir);

            var splash = new SplashWindow();
            splash.Show();
            splash.ReportStage("configuration");
            splash.ReportStage("providers");
            splash.ReportStage("devices");
            splash.CaptureRenderedFrame()!.Save(Path.Combine(dir, "splash.png"));
            splash.Close();

            var shell = new MainWindow { DataContext = new MainWindowViewModel(TestSupport.Services()) };
            shell.Show();
            shell.CaptureRenderedFrame()!.Save(Path.Combine(dir, "shell-talk.png"));
            shell.Close();

            // D2: the two playground labs, with enough fake state to show every panel.
            var stt = new SttPlaygroundViewModel(TestSupport.Services());
            stt.Cards.Insert(0, new TranscriptCard(
                "tiny.en", "Ask not what your country can do for you.", 2.1, 142,
                [.3f, .6f, .8f, .5f, .9f, .7f, .4f, .6f, .3f, .7f, .5f, .2f, .6f, .8f, .4f]));
            stt.ReferenceText = "Ask not what your country can do for you, today.";
            var sttWindow = new Window
            {
                Width = 1180, Height = 700, Background = Avalonia.Media.Brush.Parse("#0B0F14"),
                Content = new SttPlaygroundView { DataContext = stt },
            };
            sttWindow.Show();
            sttWindow.CaptureRenderedFrame()!.Save(Path.Combine(dir, "playground-stt.png"));
            sttWindow.Close();

            var tts = new TtsPlaygroundViewModel(TestSupport.Services());
            var pcm = new byte[32000];
            for (int i = 0; i < pcm.Length; i += 2) pcm[i + 1] = (byte)(0x10 + 0x30 * Math.Abs(Math.Sin(i * 0.001)));
            var take = new TtsTake("en_US-amy-low", "Piper", "Your order shipped this morning.", pcm, 16000,
                96, 0.21, Services.PcmEnvelope.Compute(pcm, 56));
            tts.Takes.Add(take);
            tts.CurrentTake = take;
            tts.PlaybackPosition = 0.42;
            var ttsWindow = new Window
            {
                Width = 1180, Height = 760, Background = Avalonia.Media.Brush.Parse("#0B0F14"),
                Content = new TtsPlaygroundView { DataContext = tts },
            };
            ttsWindow.Show();
            ttsWindow.CaptureRenderedFrame()!.Save(Path.Combine(dir, "playground-tts.png"));
            ttsWindow.Close();

            // D3: the Builder canvas — idle with selection, and live with real-shaped fake state.
            var builder = new BuilderViewModel(TestSupport.Services());
            builder.Select(builder.Nodes.First(n => n.Kind == BuilderNodeKind.Stt));
            var builderWindow = new Window
            {
                Width = 1280, Height = 760, Background = Avalonia.Media.Brush.Parse("#0B0F14"),
                Content = new BuilderView { DataContext = builder },
            };
            builderWindow.Show();
            builderWindow.CaptureRenderedFrame()!.Save(Path.Combine(dir, "builder-idle.png"));

            builder.EnqueueForTest(new Voxa.Diagnostics.VadWindowEvent(0.9f, 0.1, true, true));
            builder.EnqueueForTest(new Voxa.Diagnostics.StageLatencyEvent("vad_close", 112));
            builder.EnqueueForTest(new Voxa.Diagnostics.StageLatencyEvent("stt_final", 58));
            builder.EnqueueForTest(new Voxa.Diagnostics.StageLatencyEvent("agent_first_token", 21));
            builder.EnqueueForTest(new Voxa.Diagnostics.StageLatencyEvent("tts_first_byte", 34));
            builder.EnqueueForTest(new Voxa.Diagnostics.StageLatencyEvent("audio_out", 8));
            builder.DrainPending();
            builderWindow.CaptureRenderedFrame()!.Save(Path.Combine(dir, "builder-live.png"));
            builderWindow.Close();

            // D4: the Metrics workbench with two seeded runs, the newer selected, both compared.
            var runsDir = TestSupport.TempDir();
            var store = new RunStore(runsDir);
            store.Save(SeedRun("whispercpp·echo·piper", [620, 660, 700, 590, 640, 615, 700, 655]));
            store.Save(SeedRun("whispercpp·echo·kokoro", [430, 460, 410, 450, 480, 425, 440, 455]));
            var metrics = new MetricsViewModel(TestSupport.Services()) { RunsDirOverride = runsDir };
            metrics.Runs[0].IsChecked = true;
            metrics.Runs[1].IsChecked = true;
            var metricsWindow = new Window
            {
                Width = 1280, Height = 820, Background = Avalonia.Media.Brush.Parse("#0B0F14"),
                Content = new MetricsView { DataContext = metrics },
            };
            metricsWindow.Show();
            metricsWindow.CaptureRenderedFrame()!.Save(Path.Combine(dir, "metrics.png"));
            metricsWindow.Close();

            // VVL-001 D-equivalent: the Voices library with a saved clone, a discovered voice, and
            // the local catalogs, plus the clone wizard mid-fill.
            var voicesDir = TestSupport.TempDir();
            var vstore = new VoiceStore(voicesDir);
            vstore.Save(new VoiceProfile
            {
                DisplayName = "My Narrator", ProviderName = "ElevenLabs", ProviderVoiceId = "cloned-1",
                Kind = Voxa.Speech.Voices.VoiceKind.Cloned, ConsentAttestedAt = DateTimeOffset.Now,
            });
            var voicesVm = new VoicesViewModel(TestSupport.Services(), vstore);
            voicesVm.Catalog.CatalogOverride = name => name == "ElevenLabs"
                ? new CaptureCatalog(
                    new Voxa.Speech.Voices.ProviderVoice("cloned-1", "My Narrator", "ElevenLabs", Voxa.Speech.Voices.VoiceKind.Cloned),
                    new Voxa.Speech.Voices.ProviderVoice("rachel", "Rachel", "ElevenLabs", Voxa.Speech.Voices.VoiceKind.Standard))
                : null;
            voicesVm.RefreshCommand.Execute(null);
            voicesVm.CloneName = "New Voice";
            voicesVm.SelectedCloneTarget = "ElevenLabs";
            var voicesWindow = new Window
            {
                Width = 1280, Height = 760, Background = Avalonia.Media.Brush.Parse("#0B0F14"),
                Content = new VoicesView { DataContext = voicesVm },
            };
            voicesWindow.Show();
            voicesWindow.CaptureRenderedFrame()!.Save(Path.Combine(dir, "voices.png"));
            voicesWindow.Close();
        }
        finally
        {
            MotionSettings.SetOverride(null);
        }
    }

    /// <summary>A controlled live catalog for the capture utility (no network).</summary>
    private sealed class CaptureCatalog(params Voxa.Speech.Voices.ProviderVoice[] voices)
        : Voxa.Speech.Voices.IVoiceCatalogProvider
    {
        public Task<IReadOnlyList<Voxa.Speech.Voices.ProviderVoice>> ListVoicesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Voxa.Speech.Voices.ProviderVoice>>(voices);
    }

    /// <summary>A realistic-shaped run for the capture: VAD-dominant turns with TTS chunks.</summary>
    private static RunBundle SeedRun(string label, int[] ttfbs)
    {
        var events = new List<RunEvent>();
        long t = 0;
        foreach (var ttfb in ttfbs)
        {
            // The real stream order: stages close on the FIRST chunk; the reply keeps streaming
            // after audio_out, and BotStopped delimits it.
            events.Add(new RunEvent { Micros = t + 500_000, Kind = "stage", Stage = "vad_close", Ms = ttfb * 0.55 });
            events.Add(new RunEvent { Micros = t + 600_000, Kind = "stage", Stage = "stt_final", Ms = ttfb * 0.30 });
            events.Add(new RunEvent { Micros = t + 700_000, Kind = "stage", Stage = "agent_first_token", Ms = ttfb * 0.02 });
            events.Add(new RunEvent { Micros = t + 800_000, Kind = "stage", Stage = "tts_first_byte", Ms = ttfb * 0.11 });
            events.Add(new RunEvent { Micros = t + 900_000, Kind = "stage", Stage = "audio_out", Ms = ttfb * 0.02 });
            events.Add(new RunEvent { Micros = t + 900_000, Kind = "tts", Bytes = 48000, SampleRate = 16000 });
            events.Add(new RunEvent { Micros = t + 1_300_000, Kind = "tts", Bytes = 32000, SampleRate = 16000 });
            events.Add(new RunEvent { Micros = t + 1_400_000, Kind = "turn", Edge = "BotStopped" });
            t += 3_000_000;
        }
        var bundle = new RunBundle
        {
            Label = label,
            StartedAt = DateTimeOffset.Now,
            DurationSeconds = 46,
            SourceDescription = "scripted · 8 utterance(s) · 1500 ms gaps",
            Events = events,
        };
        bundle.Stats = RunStats.Compute(events);
        return bundle;
    }
}
