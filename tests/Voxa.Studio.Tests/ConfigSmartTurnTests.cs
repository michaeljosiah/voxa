using Microsoft.Extensions.DependencyInjection;
using Voxa.Audio.SmartTurn;
using Voxa.Speech;
using Voxa.Studio.Audio;
using Voxa.Studio.Services;
using Voxa.Studio.ViewModels;

namespace Voxa.Studio.Tests;

/// <summary>
/// The Config "Smart turn detection" toggle (P0): off by default and zero-cost (no classifier
/// registered), each provider writes its own complete config block, a half-filled form stays inert,
/// and applying the draft actually registers an <see cref="ISmartTurnClassifier"/> in the live container.
/// </summary>
public class ConfigSmartTurnTests
{
    [Fact]
    public async Task Off_By_Default_Writes_No_Keys_And_Registers_No_Classifier()
    {
        await using var services = TestSupport.Services();
        var vm = new ConfigViewModel(services);

        Assert.False(vm.SmartTurnEnabled);
        Assert.DoesNotContain(vm.DraftPairs(includeSecrets: true).Keys,
            k => k.Contains("SmartTurn", StringComparison.OrdinalIgnoreCase));
        Assert.Null(services.Provider.GetService<ISmartTurnClassifier>());   // zero-cost when off
    }

    [Fact]
    public async Task Enabling_Sidecar_Writes_The_Sidecar_Block_And_Registers_It_Live()
    {
        await using var services = TestSupport.Services();
        var vm = new ConfigViewModel(services) { SmartTurnEnabled = true }; // Sidecar is the default

        var pairs = vm.DraftPairs(includeSecrets: true);
        Assert.Equal("Sidecar", pairs["Voxa:SmartTurn:Provider"]);
        Assert.Equal("python", pairs["Voxa:SmartTurn:PythonExe"]);
        Assert.Equal("sidecar/voxa_smart_turn_sidecar.py", pairs["Voxa:SmartTurn:PythonScript"]);
        Assert.DoesNotContain("Voxa:SmartTurn:Endpoint", pairs.Keys);

        // Registering must NOT launch the process (construction is lazy) — this resolves instantly.
        services.Reconfigure(pairs);
        Assert.IsType<SidecarSmartTurnClassifier>(services.Provider.GetService<ISmartTurnClassifier>());
    }

    [Fact]
    public async Task Enabling_Http_Writes_The_Endpoint_Block_And_Registers_It_Live()
    {
        await using var services = TestSupport.Services();
        var vm = new ConfigViewModel(services)
        {
            SmartTurnEnabled = true,
            SelectedSmartTurnProvider = "Http",
            SmartTurnEndpoint = "http://localhost:8000/predict",
        };

        var pairs = vm.DraftPairs(includeSecrets: true);
        Assert.Equal("Http", pairs["Voxa:SmartTurn:Provider"]);
        Assert.Equal("http://localhost:8000/predict", pairs["Voxa:SmartTurn:Endpoint"]);
        Assert.DoesNotContain("Voxa:SmartTurn:PythonScript", pairs.Keys);

        services.Reconfigure(pairs);
        Assert.IsType<HttpSmartTurnClassifier>(services.Provider.GetService<ISmartTurnClassifier>());
    }

    [Fact]
    public async Task A_Half_Filled_Toggle_Stays_Inert()
    {
        await using var services = TestSupport.Services();
        var vm = new ConfigViewModel(services)
        {
            SmartTurnEnabled = true,
            SelectedSmartTurnProvider = "Http",
            SmartTurnEndpoint = "   ",   // enabled but no endpoint
        };

        Assert.True(vm.SmartTurnIncomplete);
        Assert.DoesNotContain(vm.DraftPairs(includeSecrets: true).Keys,
            k => k.Contains("SmartTurn", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Seeds_The_Toggle_From_A_Configured_Pipeline()
    {
        var config = TestSupport.LocalConfig(null,
            ("Voxa:SmartTurn:Provider", "Http"), ("Voxa:SmartTurn:Endpoint", "http://host:9/predict"));
        await using var services = new StudioServices(config, new NullAudioDevice(),
            new MemorySecretsStore(), new ProviderActivationStore(TestSupport.TempActivationsPath()),
            new PipelineProfileStore(TestSupport.TempProfilesPath()));

        var vm = new ConfigViewModel(services);

        Assert.True(vm.SmartTurnEnabled);
        Assert.Equal("Http", vm.SelectedSmartTurnProvider);
        Assert.Equal("http://host:9/predict", vm.SmartTurnEndpoint);
        // A configured pipeline registers the classifier from the first build — no Apply needed.
        Assert.IsType<HttpSmartTurnClassifier>(services.Provider.GetService<ISmartTurnClassifier>());
    }
}
