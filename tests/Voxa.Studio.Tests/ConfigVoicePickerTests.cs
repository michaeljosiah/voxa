using Voxa.Speech.Voices;
using Voxa.Studio.Audio;
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

    // A catalog whose list completes only when the gate is released — to drive the in-flight race.
    private sealed class GatedCatalog(Task gate, params ProviderVoice[] voices) : IVoiceCatalogProvider
    {
        public async Task<IReadOnlyList<ProviderVoice>> ListVoicesAsync(CancellationToken ct)
        {
            await gate;
            return voices;
        }
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

    [Fact] // review fix — no network at startup, even when the configured TTS is a cloud provider
    public async Task Constructing_With_A_Cloud_Tts_Does_Not_Load_Voices_At_Startup()
    {
        // A cloud TTS with a (fake) key: if the ctor eagerly loaded, it would make a live HTTP call
        // before the user acts — the "Studio never touches the network before the user acts" rule.
        var config = TestSupport.LocalConfig(null,
            ("Voxa:Tts", "ElevenLabs"), ("Voxa:ElevenLabs:ApiKey", "fake-key"));
        await using var services = new StudioServices(config, new NullAudioDevice(),
            new MemorySecretsStore(), new ProviderActivationStore(TestSupport.TempActivationsPath()),
            new PipelineProfileStore(TestSupport.TempProfilesPath()));

        var vm = new ConfigViewModel(services);

        Assert.Empty(vm.CloudVoices);            // nothing loaded at construction
        Assert.True(vm.ShowCloudVoiceOptions);   // …but it WOULD load on demand

        // On a user action (open Config / change TTS) it loads — here with a controlled catalog.
        vm.VoiceCatalog.CatalogOverride = _ => new FakeCatalog(
            new ProviderVoice("v1", "V1", "ElevenLabs", VoiceKind.Standard));
        await vm.ReloadCloudVoicesAsync();
        Assert.Equal(["v1"], vm.CloudVoices);
    }

    [Fact] // review fix (P2) — a provider switch mid-request discards the stale result, no NRE
    public async Task Switching_Provider_While_A_Voice_List_Is_In_Flight_Discards_The_Stale_Result()
    {
        await using var services = TestSupport.Services();
        var vm = new ConfigViewModel(services);

        var gate = new TaskCompletionSource();
        vm.VoiceCatalog.CatalogOverride = name => name == "ElevenLabs"
            ? new GatedCatalog(gate.Task, new ProviderVoice("el-late", "Late", "ElevenLabs", VoiceKind.Standard))
            : null;

        vm.SelectedTts = "ElevenLabs";
        var pending = vm.ReloadCloudVoicesAsync();   // starts for ElevenLabs, parks on the gate

        vm.SelectedTts = "Piper";                    // user switches to a local provider mid-flight
        gate.SetResult();                            // the ElevenLabs list now returns
        await pending;                               // must not throw, must not write stale voices

        Assert.False(vm.ShowCloudVoiceOptions);      // now on Piper (static picker)
        Assert.DoesNotContain("el-late", vm.CloudVoices);
    }
}
