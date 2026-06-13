using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Voxa.Studio.Services;
using Voxa.Studio.Theme;
using Voxa.Studio.ViewModels;
using Voxa.Studio.Views;

namespace Voxa.Studio;

public class App : Application
{
    /// <summary>Test hook: headless tests inject pre-built services and skip the splash.</summary>
    public StudioServices? ServicesOverride { get; set; }

    private StudioServices? _services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Apply the saved theme before any window shows — live brush mutation, default Warm.
            ThemeManager.Apply(StudioThemes.ById(StudioPreferences.Load().ThemeId));

            if (ServicesOverride is not null)
            {
                // Headless/test path: no splash, services were built by the caller.
                _services = ServicesOverride;
                desktop.MainWindow = new MainWindow
                {
                    DataContext = new MainWindowViewModel(_services),
                };
            }
            else
            {
                BootWithSplash(desktop);
            }

            // Dispose asynchronously without blocking the UI thread. A sync `.GetResult()` here
            // would deadlock if any disposal path ever marshals back to the dispatcher (WASAPI
            // teardown, a future processor's cleanup): defer the exit instead — cancel the first
            // shutdown request, run DisposeAsync, then re-request shutdown once it completes.
            var disposing = false;
            desktop.ShutdownRequested += (_, e) =>
            {
                var services = _services;
                if (disposing || services is null) return; // second pass (or nothing to dispose): let it through
                disposing = true;
                e.Cancel = true;
                _ = services.DisposeAsync().AsTask().ContinueWith(
                    _ => Dispatcher.UIThread.Post(() => desktop.Shutdown()));
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// The §4 launch sequence: splash appears immediately (before any Voxa service work),
    /// the real boot runs on a background thread reporting stage names, and the shell opens
    /// with the mark already seated in the titlebar, so the logo "lands" rather than disappears.
    ///
    /// <para>
    /// The brand intro runs ~2.2&#160;s, but the local boot (config + providers + device
    /// enumeration + cache scan) finishes well inside that on a fast machine — so a naive
    /// "close the moment init completes" cuts the animation off before anyone sees it. We hold
    /// the splash for a minimum (<see cref="MinimumOnScreen"/>) so the mark and wordmark actually
    /// play. The hold is skipped under reduce-motion (no animation to wait for) and a click
    /// bypasses it (a deliberate skip should land in the shell at once). If the boot itself
    /// takes longer than the minimum, there is no extra wait.
    /// </para>
    /// </summary>
    private void BootWithSplash(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var splash = new SplashWindow();
        desktop.MainWindow = splash;
        splash.Show();

        var shown = Stopwatch.StartNew();
        var minimumOnScreen = MotionSettings.ReduceMotion ? TimeSpan.Zero : MinimumOnScreen;

        var bootDone = false;
        var skipped = false;
        DispatcherTimer? hold = null;

        void SwapToShell()
        {
            if (!bootDone || _services is null) return;                 // still booting
            if (!skipped && shown.Elapsed < minimumOnScreen) return;    // let the intro breathe
            hold?.Stop();

            var main = new MainWindow
            {
                DataContext = new MainWindowViewModel(_services),
            };
            desktop.MainWindow = main;
            main.Show();
            splash.Close();
        }

        splash.SkipRequested += () => { skipped = true; SwapToShell(); };

        Task.Run(() => StartupCoordinator.Run(
                stage => Dispatcher.UIThread.Post(() => splash.ReportStage(stage))))
            .ContinueWith(boot => Dispatcher.UIThread.Post(() =>
            {
                if (boot.IsFaulted)
                {
                    // Honest failure beats a hung splash: show the root cause where the
                    // microcopy was; the user closes the window to exit.
                    splash.ShowError(boot.Exception!.GetBaseException().Message);
                    return;
                }

                _services = boot.Result;
                bootDone = true;

                var remaining = minimumOnScreen - shown.Elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    hold = new DispatcherTimer { Interval = remaining };
                    hold.Tick += (_, _) => { hold!.Stop(); SwapToShell(); };
                    hold.Start();
                }
                SwapToShell();
            }));
    }

    /// <summary>How long the splash stays up at minimum, so the brand intro (~2.2&#160;s) completes
    /// plus a short beat to see it seated. Skipped under reduce-motion and on a user click.</summary>
    private static readonly TimeSpan MinimumOnScreen = TimeSpan.FromSeconds(2.8);
}
