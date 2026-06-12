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
        }
        finally
        {
            MotionSettings.SetOverride(null);
        }
    }
}
