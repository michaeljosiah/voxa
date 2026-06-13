using Voxa.Studio.Services;
using Voxa.Studio.ViewModels;

namespace Voxa.Studio.Tests;

/// <summary>VST-003 WS4: the Settings view-models — list state, the working-copy clone gate, status,
/// and Save/Cancel persistence. All headless, no Avalonia.</summary>
public class SettingsViewModelTests
{
    private static ProviderSecretsService Service(ISecretsStore? store = null) =>
        new(store ?? new MemorySecretsStore(), new ProviderActivationStore(TestSupport.TempActivationsPath()));

    [Fact] // WS4-A1
    public void Fresh_Tab_Lists_Locals_And_Offers_Cloud_Identities()
    {
        var vm = new ProvidersViewModel(Service());

        var localNames = vm.Rows.Where(r => r.IsLocal).Select(r => r.Manifest.Name).OrderBy(n => n);
        Assert.Equal(["Echo", "Kokoro", "Piper", "WhisperCpp"], localNames);

        var available = vm.Available.Select(m => m.Name).OrderBy(n => n);
        Assert.Equal(["Azure", "ElevenLabs", "Mistral", "OpenAI"], available);
    }

    [Fact] // WS4-A2
    public void Adding_OpenAI_Shows_All_Roles_And_Goes_Green_When_Keyed()
    {
        var vm = new ProvidersViewModel(Service());
        vm.AddProvider("OpenAI");

        var row = vm.Rows.Single(r => r.Manifest.Name == "OpenAI");
        Assert.DoesNotContain(vm.Available, m => m.Name == "OpenAI");
        Assert.Equal("STT · TTS · Agent", row.RolesLabel);
        Assert.Equal(ProviderStatus.KeyMissing, row.Status);

        row.Fields.Single().Value = "sk-123";
        Assert.Equal(ProviderStatus.Configured, row.Status);
    }

    [Fact] // WS4-A3
    public void Removing_A_Local_Provider_Is_A_No_Op()
    {
        var vm = new ProvidersViewModel(Service());
        var before = vm.Rows.Count;

        vm.RemoveProvider("Piper");

        Assert.Equal(before, vm.Rows.Count);
        Assert.Contains(vm.Rows, r => r.Manifest.Name == "Piper");
    }

    [Fact] // WS4-A4
    public void Save_Persists_Activation_And_Field_Values()
    {
        var store = new MemorySecretsStore();
        var activations = new ProviderActivationStore(TestSupport.TempActivationsPath());
        var secrets = new ProviderSecretsService(store, activations);

        var settings = new SettingsViewModel(secrets);
        settings.Providers.AddProvider("ElevenLabs");
        settings.Providers.Rows.Single(r => r.Manifest.Name == "ElevenLabs").Fields.Single().Value = "sk_99";
        settings.Save();

        Assert.True(settings.Saved);

        var reloaded = new ProviderSecretsService(store, activations);   // fresh service, same stores
        Assert.Contains(reloaded.Activated, m => m.Name == "ElevenLabs");
        Assert.Equal("sk_99", reloaded.GetSecret("ElevenLabs", "ApiKey"));
    }

    [Fact] // WS4-A5
    public void Cancel_Leaves_The_Persisted_Value_Unchanged()
    {
        var store = new MemorySecretsStore(new Dictionary<string, string> { ["ElevenLabs:ApiKey"] = "original" });
        var activations = new ProviderActivationStore(TestSupport.TempActivationsPath());
        activations.Save([new ProviderActivation("ElevenLabs", DateTimeOffset.UtcNow)]);
        var secrets = new ProviderSecretsService(store, activations);

        var settings = new SettingsViewModel(secrets);
        settings.Providers.Rows.Single(r => r.Manifest.Name == "ElevenLabs").Fields.Single().Value = "changed";
        settings.Cancel();

        Assert.False(settings.Saved);
        Assert.Equal("original", secrets.GetSecret("ElevenLabs", "ApiKey"));   // untouched
    }

    [Fact] // WS4-A6
    public void Reveal_Toggle_Masks_Without_Changing_The_Value()
    {
        var vm = new ProvidersViewModel(Service());
        vm.AddProvider("ElevenLabs");
        var field = vm.Rows.Single(r => r.Manifest.Name == "ElevenLabs").Fields.Single();

        field.Value = "secret";
        field.IsRevealed = true;
        Assert.Equal("secret", field.Value);
        Assert.Equal('\0', field.EffectivePasswordChar);   // revealed → unmasked

        field.IsRevealed = false;
        Assert.Equal('●', field.EffectivePasswordChar);    // hidden → masked
    }

    [Fact] // WS4-A7
    public void Azure_Has_Two_Fields_With_A_NonSecret_Region()
    {
        var vm = new ProvidersViewModel(Service());
        vm.AddProvider("Azure");

        var fields = vm.Rows.Single(r => r.Manifest.Name == "Azure").Fields;
        Assert.Equal(2, fields.Count);
        Assert.Contains(fields, f => f.Descriptor.Name == "Region" && !f.Descriptor.IsSecret);
    }
}
