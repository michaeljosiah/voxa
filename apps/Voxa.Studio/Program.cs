using Avalonia;

namespace Voxa.Studio;

internal static class Program
{
    // Avalonia configuration is in BuildAvaloniaApp — keep Main free of Avalonia types so the
    // previewer and headless tests can use the same entry point.
    [STAThread]
    public static void Main(string[] args)
        => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
