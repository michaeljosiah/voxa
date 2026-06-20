using Voxa.Studio.Audio;
using Voxa.Studio.Services;
using Voxa.Studio.ViewModels;

namespace Voxa.Studio.Tests;

/// <summary>
/// Config's draft layers over the live config, so a picker set back to its default must emit that default
/// EXPLICITLY when the base config pinned a non-default value — otherwise the omitted key falls back to the
/// base through config layering and the picker can never revert it. Verified here for the profile and VAD
/// pickers (the same explicit-override rule as AEC/denoise/smart-turn and the compute-device pickers).
/// </summary>
public class ConfigBaseOverrideTests
{
    [Fact]
    public async Task Selecting_Default_Profile_And_Silero_Vad_Overrides_A_Base_Config()
    {
        var config = TestSupport.LocalConfig(null,
            ("Voxa:Profile", "Quality"), ("Voxa:Vad:Engine", "None"));
        await using var services = new StudioServices(config, new NullAudioDevice(),
            new MemorySecretsStore(), new ProviderActivationStore(TestSupport.TempActivationsPath()),
            new PipelineProfileStore(TestSupport.TempProfilesPath()));
        var vm = new ConfigViewModel(services);

        Assert.Equal("Quality", vm.SelectedProfile);   // seeded from the base config
        Assert.Equal("None", vm.SelectedVad);

        vm.SelectedProfile = "Default";                 // user reverts both to the default
        vm.SelectedVad = "Silero";

        var pairs = vm.DraftPairs();
        Assert.Equal("Default", pairs["Voxa:Profile"]);     // explicit, beating the base Quality
        Assert.Equal("Silero", pairs["Voxa:Vad:Engine"]);   // explicit, beating the base None
    }

    [Fact]
    public void Default_Profile_And_Silero_Vad_With_No_Base_Omit_The_Keys()
    {
        // A plain local config (no profile / Silero VAD) keeps the export minimal — the defaults aren't emitted.
        var vm = new ConfigViewModel(TestSupport.Services())
        {
            SelectedProfile = "Default",
            SelectedVad = "Silero",
        };

        var pairs = vm.DraftPairs();
        Assert.False(pairs.ContainsKey("Voxa:Profile"));
        Assert.False(pairs.ContainsKey("Voxa:Vad:Engine"));
    }
}
