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
        Assert.NotNull(vm.Playgrounds);
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

    [Fact]
    public async Task TalkSession_Composes_The_Keyless_Pipeline_Without_AspNet()
    {
        // Regression: Studio is a plain ServiceCollection — ASP.NET's implicit IConfiguration
        // registration is absent. The meta-package's DefaultAgentFactory must therefore capture
        // the configuration passed to AddVoxa instead of resolving it from DI, or composing the
        // shipped Echo config throws at the very first Talk start. Composing here exercises the
        // full part chain (VAD, STT, taps, agent via factory, aggregator, TTS) with no network.
        var services = TestSupport.Services();
        await using var session = services.CreateTalkSession();

        Assert.Equal(16000, session.InputSampleRate);   // WhisperCpp
        Assert.Equal(16000, session.OutputSampleRate);  // en_US-amy-low
        Assert.NotNull(session.Hub);
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
