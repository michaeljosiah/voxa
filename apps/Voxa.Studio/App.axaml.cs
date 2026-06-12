using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Voxa.Studio.Services;
using Voxa.Studio.ViewModels;
using Voxa.Studio.Views;

namespace Voxa.Studio;

public class App : Application
{
    /// <summary>Test hook: headless tests inject pre-built services before the framework initializes.</summary>
    public StudioServices? ServicesOverride { get; set; }

    private StudioServices? _services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        _services = ServicesOverride ?? new StudioServices();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(_services),
            };
            desktop.ShutdownRequested += (_, _) => _services.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
