using Voxa.Studio.Audio;
using Voxa.Studio.Services;
using Voxa.Studio.ViewModels;

namespace Voxa.Studio.Tests;

/// <summary>
/// The Config view surfaces the AEC (VRT-003) and denoise (VLS-004) seams as registry-driven engine
/// pickers. Stock Studio bundles no concrete engine, so the pickers offer only "None" and the card shows a
/// how-to-enable hint; a selected engine round-trips into Voxa:Aec:Engine / Voxa:Enhance:Engine.
/// </summary>
public class ConfigInputCleanupTests
{
    [Fact]
    public void No_Engine_Bundled_Offers_Only_None_And_Emits_Nothing()
    {
        var vm = new ConfigViewModel(TestSupport.Services());

        Assert.Equal(new[] { "None" }, vm.AecEngines);
        Assert.Equal(new[] { "None" }, vm.EnhanceEngines);
        Assert.False(vm.AecEngineAvailable);
        Assert.False(vm.EnhanceEngineAvailable);
        Assert.True(vm.ShowAudioCleanupHint); // the card explains how to add an engine instead of dead pickers

        var pairs = vm.DraftPairs();
        Assert.False(pairs.ContainsKey("Voxa:Aec:Engine"));
        Assert.False(pairs.ContainsKey("Voxa:Enhance:Engine"));
    }

    [Fact]
    public void Selecting_An_Engine_Emits_The_Config_Key()
    {
        // The pickers are registry-driven; if an external AEC/denoise provider were registered the
        // selection would flow through to config exactly like this (proven without bundling one).
        var vm = new ConfigViewModel(TestSupport.Services())
        {
            SelectedAecEngine = "WebRtc",
            SelectedEnhanceEngine = "DeepFilterNet3",
        };

        var pairs = vm.DraftPairs();
        Assert.Equal("WebRtc", pairs["Voxa:Aec:Engine"]);
        Assert.Equal("DeepFilterNet3", pairs["Voxa:Enhance:Engine"]);
    }

    [Fact] // codex P2: selecting "None" must override a base-config engine, not silently fall back to it.
    public async Task Disabling_Overrides_A_Base_Config_Cleanup_Engine()
    {
        var config = TestSupport.LocalConfig(null,
            ("Voxa:Aec:Engine", "WebRtc"), ("Voxa:Enhance:Engine", "DeepFilterNet3"));
        await using var services = new StudioServices(config, new NullAudioDevice(),
            new MemorySecretsStore(), new ProviderActivationStore(TestSupport.TempActivationsPath()),
            new PipelineProfileStore(TestSupport.TempProfilesPath()));
        var vm = new ConfigViewModel(services);

        Assert.Equal("WebRtc", vm.SelectedAecEngine);          // seeded from the base config
        Assert.Equal("DeepFilterNet3", vm.SelectedEnhanceEngine);

        vm.SelectedAecEngine = "None";                          // user disables both
        vm.SelectedEnhanceEngine = "None";

        var pairs = vm.DraftPairs();
        Assert.Equal("None", pairs["Voxa:Aec:Engine"]);        // explicit off, beating the base value
        Assert.Equal("None", pairs["Voxa:Enhance:Engine"]);
    }
}
