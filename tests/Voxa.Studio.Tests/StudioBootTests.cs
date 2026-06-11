using Avalonia.Headless.XUnit;
using Voxa.Studio.ViewModels;
using Voxa.Studio.Views;

namespace Voxa.Studio.Tests;

/// <summary>
/// VST-001 WS1-A1/A2: the shell boots keyless on the headless platform — XAML resources resolve,
/// the four sections construct, the registry has the local tier, and nothing touches the network
/// (the isolated temp cache stays empty).
/// </summary>
public class StudioBootTests
{
    [AvaloniaFact]
    public void MainWindow_Boots_Keyless_With_Four_Sections_And_No_Network()
    {
        var cacheRoot = TestSupport.TempDir();
        var services = TestSupport.Services(cacheRoot);
        var vm = new MainWindowViewModel(services);

        var window = new MainWindow { DataContext = vm };
        window.Show(); // resolves every StaticResource + compiled binding in the shell

        Assert.NotNull(vm.Talk);
        Assert.NotNull(vm.Voices);
        Assert.NotNull(vm.Models);
        Assert.NotNull(vm.Config);

        // The keyless local tier resolved from the registry (WS1-A2).
        Assert.Contains("WhisperCpp", services.Registry.SttNames);
        Assert.Contains("Piper", services.Registry.TtsNames);
        Assert.Contains("Kokoro", services.Registry.TtsNames);
        Assert.Contains("WhisperCpp", vm.Talk.ProviderChain);
        Assert.Contains("Echo", vm.Talk.ProviderChain);

        // No model bytes were fetched by merely booting (downloads are user-triggered).
        Assert.Empty(Directory.EnumerateFileSystemEntries(cacheRoot));

        window.Close();
    }

    [AvaloniaFact]
    public void Section_Views_All_Construct()
    {
        var vm = new MainWindowViewModel(TestSupport.Services());
        var window = new MainWindow { DataContext = vm };
        window.Show();

        // Switching sections must not throw (each view's bindings resolve against its VM).
        foreach (var section in new[] { 1, 2, 3, 0 })
            vm.SelectedSection = section;

        window.Close();
    }
}
