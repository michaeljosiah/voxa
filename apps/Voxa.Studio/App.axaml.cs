using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Voxa.Studio.Services;
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
    /// the real boot runs on a background thread reporting stage names, and the splash is
    /// dismissed the moment init completes — even mid-animation. The shell window opens with
    /// the mark already seated in the titlebar, so the logo "lands" rather than disappears.
    /// </summary>
    private void BootWithSplash(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var splash = new SplashWindow();
        desktop.MainWindow = splash;
        splash.Show();

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
                var main = new MainWindow
                {
                    DataContext = new MainWindowViewModel(_services),
                };
                desktop.MainWindow = main;
                main.Show();
                splash.Close();
            }));
    }
}
