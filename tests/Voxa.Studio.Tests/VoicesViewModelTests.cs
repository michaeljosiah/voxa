using Voxa.Speech.Voices;
using Voxa.Studio.Services;
using Voxa.Studio.ViewModels;

namespace Voxa.Studio.Tests;

/// <summary>
/// VVL-001 WS5: the Voices section view-model — library states + provider chips, the consent-gated
/// clone command, clone persistence with attestation, and shell device arbitration. Headless.
/// </summary>
public class VoicesViewModelTests
{
    private static VoiceProfile Profile(string name, string provider, string voiceId) => new()
    {
        DisplayName = name, ProviderName = provider, ProviderVoiceId = voiceId, Kind = VoiceKind.Cloned,
    };

    private sealed class FakeCatalog(params ProviderVoice[] voices) : IVoiceCatalogProvider
    {
        public Task<IReadOnlyList<ProviderVoice>> ListVoicesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ProviderVoice>>(voices);
    }

    private sealed class FakeCloner(ProviderVoice? result = null, string? throwMessage = null) : IVoiceCloneProvider
    {
        public int Creates { get; private set; }
        public Task<ProviderVoice> CreateVoiceAsync(VoiceCloneRequest request, CancellationToken ct)
        {
            Creates++;
            if (throwMessage is not null) throw new VoiceProviderException(throwMessage);
            return Task.FromResult(result ?? new ProviderVoice("new-id", request.Name, "ElevenLabs", VoiceKind.Cloned));
        }
        public Task DeleteVoiceAsync(string voiceId, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact] // WS5-A1
    public async Task Library_Renders_States_And_Provider_Chips()
    {
        var store = new VoiceStore(TestSupport.TempDir());
        store.Save(Profile("Saved & Live", "ElevenLabs", "live-1"));

        await using var services = TestSupport.Services();
        var vm = new VoicesViewModel(services, store);
        vm.Catalog.CatalogOverride = name => name == "ElevenLabs"
            ? new FakeCatalog(
                new ProviderVoice("live-1", "Saved & Live", "ElevenLabs", VoiceKind.Cloned),
                new ProviderVoice("stock-1", "Rachel", "ElevenLabs", VoiceKind.Standard))
            : null;

        await vm.RefreshCommand.ExecuteAsync(null);

        // ElevenLabs (overridden) has a key; Mistral (no key, no override) reports missing.
        Assert.False(vm.Providers.Single(p => p.Provider == "ElevenLabs").MissingKey);
        Assert.True(vm.Providers.Single(p => p.Provider == "Mistral").MissingKey);

        var byId = vm.Voices.ToDictionary(v => v.Voice.Id);
        Assert.Equal(VoiceState.Live, byId["live-1"].State);
        Assert.Equal(VoiceState.Discovered, byId["stock-1"].State);
        Assert.Contains(vm.Voices, v => v.State == VoiceState.LocalCatalog);   // Piper/Kokoro catalogs
    }

    [Fact] // WS5-A2 — the consent gate is on the command, not just the UI
    public void Clone_Command_Is_Blocked_Until_Name_Sample_Target_And_Consent_Are_All_Present()
    {
        var vm = new VoicesViewModel(TestSupport.Services(), new VoiceStore(TestSupport.TempDir()));

        Assert.False(vm.CloneCommand.CanExecute(null));          // nothing set
        vm.CloneName = "My Narrator";
        Assert.False(vm.CloneCommand.CanExecute(null));          // no sample/target/consent
        vm.AddSample(new VoiceSample("ref.wav", new byte[] { 1, 2 }));
        vm.SelectedCloneTarget = "ElevenLabs";
        Assert.False(vm.CloneCommand.CanExecute(null));          // still no consent

        vm.ConsentAttested = true;
        Assert.True(vm.CloneCommand.CanExecute(null));           // all four present

        vm.ConsentAttested = false;
        Assert.False(vm.CloneCommand.CanExecute(null));          // un-ticking re-blocks it
    }

    [Fact] // WS5-A3
    public async Task A_Successful_Clone_Saves_A_Profile_With_Consent_And_Samples_Then_Resets()
    {
        var store = new VoiceStore(TestSupport.TempDir());
        await using var services = TestSupport.Services();
        var vm = new VoicesViewModel(services, store);
        vm.Catalog.ClonerOverride = _ => new FakeCloner(
            new ProviderVoice("cloned-77", "My Narrator", "ElevenLabs", VoiceKind.Cloned));

        vm.CloneName = "My Narrator";
        vm.AddSample(new VoiceSample("ref.wav", new byte[] { 1, 2, 3, 4 }));
        vm.SelectedCloneTarget = "ElevenLabs";
        vm.ConsentAttested = true;

        await vm.CloneCommand.ExecuteAsync(null);

        var profile = Assert.Single(store.Load(), p => p.ProviderVoiceId == "cloned-77");
        Assert.NotNull(profile.ConsentAttestedAt);          // attestation recorded
        Assert.Single(profile.SamplePaths);                 // the reference clip was persisted

        // The wizard resets after a successful clone.
        Assert.Equal("", vm.CloneName);
        Assert.False(vm.ConsentAttested);
        Assert.Empty(vm.CloneSamples);
        Assert.Null(vm.CloneError);
    }

    [Fact] // WS5-A3 — a rejected clone surfaces the message and leaves the store untouched
    public async Task A_Rejected_Clone_Sets_The_Error_And_Saves_Nothing()
    {
        var store = new VoiceStore(TestSupport.TempDir());
        await using var services = TestSupport.Services();
        var vm = new VoicesViewModel(services, store);
        vm.Catalog.ClonerOverride = _ => new FakeCloner(throwMessage: "can_not_use_instant_voice_cloning");

        vm.CloneName = "Nope";
        vm.AddSample(new VoiceSample("ref.wav", new byte[] { 1 }));
        vm.SelectedCloneTarget = "ElevenLabs";
        vm.ConsentAttested = true;

        await vm.CloneCommand.ExecuteAsync(null);

        Assert.Contains("instant_voice_cloning", vm.CloneError);
        Assert.Empty(store.Load());                 // nothing persisted
        Assert.True(vm.ConsentAttested);            // wizard preserved so the user can adjust + retry
    }

    [Fact] // WS5-A4 — device arbitration through the shell
    public void A_Live_Run_Blocks_Sample_Recording_And_Clears_When_It_Stops()
    {
        var shell = new MainWindowViewModel(TestSupport.Services());
        Assert.False(shell.Voices.RecordBlocked);

        shell.Builder.IsRunning = true;             // a live run takes the one audio device
        Assert.True(shell.Voices.RecordBlocked);

        shell.Builder.IsRunning = false;
        Assert.False(shell.Voices.RecordBlocked);
    }

    [Fact] // review fix — auditioning a local voice opens the TTS lab with that voice selected
    public void Auditioning_A_Local_Voice_Opens_The_Tts_Lab_With_That_Voice_Selected()
    {
        var shell = new MainWindowViewModel(TestSupport.Services());
        var amy = new LibraryVoice(
            new ProviderVoice("en_US-amy-low", "en_US-amy-low", "Piper", VoiceKind.Standard),
            VoiceState.LocalCatalog);

        shell.Voices.AuditionCommand.Execute(amy);

        Assert.Equal(1, shell.SelectedSection);              // Playgrounds section
        Assert.Equal(1, shell.Playgrounds.SelectedLab);      // TTS lab
        Assert.Equal("en_US-amy-low", shell.Playgrounds.Tts.SelectedVoice?.Name);   // preselected for real
    }

    [Fact] // provider filter narrows the library to one provider
    public async Task Provider_Filter_Narrows_The_Library_To_One_Provider()
    {
        var store = new VoiceStore(TestSupport.TempDir());
        await using var services = TestSupport.Services();
        var vm = new VoicesViewModel(services, store);
        vm.Catalog.CatalogOverride = name => name == "ElevenLabs"
            ? new FakeCatalog(new ProviderVoice("stock-1", "Rachel", "ElevenLabs", VoiceKind.Standard))
            : null;

        await vm.RefreshCommand.ExecuteAsync(null);

        // Every provider present in the library is offered (plus the "All" sentinel); unfiltered shows all.
        Assert.Equal("All providers", vm.SelectedProviderFilter);
        Assert.Contains("ElevenLabs", vm.ProviderFilters);
        Assert.Contains("Piper", vm.ProviderFilters);   // local catalog voices
        Assert.Equal(vm.Voices.Count, vm.VisibleVoices.Count);

        // Filter to one provider → only its voices.
        vm.SelectedProviderFilter = "ElevenLabs";
        Assert.NotEmpty(vm.VisibleVoices);
        Assert.All(vm.VisibleVoices, v => Assert.Equal("ElevenLabs", v.Voice.ProviderName));

        // Back to "All providers" → the whole library again.
        vm.SelectedProviderFilter = "All providers";
        Assert.Equal(vm.Voices.Count, vm.VisibleVoices.Count);
    }
}
