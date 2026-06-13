using Voxa.Speech.Voices;
using Voxa.Studio.Services;
using Voxa.Studio.ViewModels;

namespace Voxa.Studio.Tests;

/// <summary>
/// VVL-001 WS6: the Config composer's voice picker hand-off — selecting a cloud TTS provider
/// repopulates the voice list from the live library, and choosing a voice writes the provider-
/// correct config key into the export, never an API key.
/// </summary>
public class ConfigVoicePickerTests
{
    private sealed class FakeCatalog(params ProviderVoice[] voices) : IVoiceCatalogProvider
    {
        public Task<IReadOnlyList<ProviderVoice>> ListVoicesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ProviderVoice>>(voices);
    }

    [Fact] // WS6-A1
    public async Task Selecting_ElevenLabs_Loads_Library_Voices_And_Writes_The_VoiceId_Key()
    {
        await using var services = TestSupport.Services();
        var vm = new ConfigViewModel(services);
        vm.VoiceCatalog.CatalogOverride = name => name == "ElevenLabs"
            ? new FakeCatalog(
                new ProviderVoice("rachel", "Rachel", "ElevenLabs", VoiceKind.Standard),
                new ProviderVoice("my-clone", "My Narrator", "ElevenLabs", VoiceKind.Cloned))
            : null;

        vm.SelectedTts = "ElevenLabs";
        await vm.ReloadCloudVoicesAsync();   // deterministic — bypass the fire-and-forget

        Assert.True(vm.ShowCloudVoiceOptions);
        Assert.False(vm.CloudVoiceKeyRequired);
        Assert.Equal(["rachel", "my-clone"], vm.CloudVoices);

        vm.SelectedCloudVoice = "my-clone";
        var pairs = vm.DraftPairs(includeSecrets: true);

        Assert.Equal("my-clone", pairs["Voxa:ElevenLabs:VoiceId"]);   // provider-correct key
        Assert.DoesNotContain(pairs.Keys, k => k.Contains("ApiKey", StringComparison.OrdinalIgnoreCase));
    }

    [Fact] // WS6-A1 — switching back to a local provider hides the cloud picker
    public async Task Switching_To_A_Local_Provider_Hides_The_Cloud_Picker()
    {
        await using var services = TestSupport.Services();
        var vm = new ConfigViewModel(services);
        vm.VoiceCatalog.CatalogOverride = _ => new FakeCatalog(new ProviderVoice("v", "V", "ElevenLabs", VoiceKind.Standard));

        vm.SelectedTts = "ElevenLabs";
        await vm.ReloadCloudVoicesAsync();
        Assert.True(vm.ShowCloudVoiceOptions);

        vm.SelectedTts = "Piper";
        await vm.ReloadCloudVoicesAsync();
        Assert.False(vm.ShowCloudVoiceOptions);
        Assert.Empty(vm.CloudVoices);
    }

    [Fact] // WS6-A1 — a provider with no key reports it instead of crashing
    public async Task A_Cloud_Provider_With_No_Key_Reports_Key_Required()
    {
        await using var services = TestSupport.Services();   // keyless config
        var vm = new ConfigViewModel(services);

        vm.SelectedTts = "Mistral";
        await vm.ReloadCloudVoicesAsync();

        Assert.True(vm.ShowCloudVoiceOptions);
        Assert.True(vm.CloudVoiceKeyRequired);
        Assert.Empty(vm.CloudVoices);
    }

    [Fact] // WS6-A2 — the export never carries an API key
    public async Task The_Export_Carries_The_Voice_Key_But_No_Secret()
    {
        await using var services = TestSupport.Services();
        var vm = new ConfigViewModel(services);
        vm.VoiceCatalog.CatalogOverride = _ => new FakeCatalog(new ProviderVoice("nova", "Nova", "Mistral", VoiceKind.Standard));

        vm.SelectedTts = "Mistral";
        await vm.ReloadCloudVoicesAsync();
        vm.SelectedCloudVoice = "nova";

        Assert.Contains("\"Voice\": \"nova\"", vm.ExportJson);
        Assert.DoesNotContain("ApiKey", vm.ExportJson, StringComparison.OrdinalIgnoreCase);
    }
}
