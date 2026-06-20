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
}
