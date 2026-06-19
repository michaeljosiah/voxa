using Voxa.Studio.Audio;
using Voxa.Studio.Services;
using Voxa.Studio.ViewModels;

namespace Voxa.Studio.Tests;

/// <summary>VST-003 WS6: the Config dropdown filter, the shell hand-off, and startup secret injection
/// — end-to-end against a real StudioServices.</summary>
public class SettingsIntegrationTests
{
    [Fact] // WS6-A1 — only local + activated identities show in Config
    public async Task Only_Local_And_Activated_Providers_Show_In_Config()
    {
        var activations = new ProviderActivationStore(TestSupport.TempActivationsPath());
        activations.Save([new ProviderActivation("ElevenLabs", DateTimeOffset.UtcNow)]);
        await using var services = TestSupport.Services(activations: activations);

        var config = new ConfigViewModel(services);

        Assert.Contains("ElevenLabs", config.TtsProviders);   // activated cloud
        Assert.Contains("Piper", config.TtsProviders);        // local
        Assert.Contains("Kokoro", config.TtsProviders);       // local
        Assert.DoesNotContain("OpenAI", config.TtsProviders); // not activated
        Assert.DoesNotContain("Azure", config.TtsProviders);
        Assert.DoesNotContain("Mistral", config.TtsProviders);

        Assert.Equal(["WhisperCpp"], config.SttProviders);    // only the local STT
    }

    [Fact] // WS6-A2 — activating OpenAI in Settings lights up all three Config dropdowns, no restart
    public async Task Activating_OpenAI_In_Settings_Adds_It_To_All_Three_Dropdowns()
    {
        await using var services = TestSupport.Services();
        var shell = new MainWindowViewModel(services);
        Assert.DoesNotContain("OpenAI", shell.Config.TtsProviders);

        var settings = new SettingsViewModel(services.Secrets);
        settings.Providers.AddProvider("OpenAI");
        settings.Providers.Rows.Single(r => r.Manifest.Name == "OpenAI").Fields.Single().Value = "sk-1";
        settings.Save();
        shell.OnSettingsSaved();

        Assert.Contains("OpenAI", shell.Config.SttProviders);
        Assert.Contains("OpenAI", shell.Config.TtsProviders);
        Assert.Contains("OpenAI", shell.Config.AgentProviders);
        Assert.Equal("sk-1", services.Configuration["Voxa:OpenAI:ApiKey"]);   // and it's live
    }

    [Fact] // WS6-A4 — a stored key is live from the first session (startup injection)
    public async Task A_Stored_Key_Is_Live_From_The_First_Session()
    {
        var secrets = new MemorySecretsStore(new Dictionary<string, string> { ["ElevenLabs:ApiKey"] = "sk_boot" });
        await using var services = TestSupport.Services(secrets: secrets);
        Assert.Equal("sk_boot", services.Configuration["Voxa:ElevenLabs:ApiKey"]);
    }

    [Fact] // WS6-A5 — a Config Apply keeps the stored key live, end-to-end
    public async Task A_Config_Apply_Keeps_The_Stored_Key()
    {
        var secrets = new MemorySecretsStore(new Dictionary<string, string> { ["OpenAI:ApiKey"] = "sk_keep" });
        await using var services = TestSupport.Services(secrets: secrets);

        var config = new ConfigViewModel(services);
        config.SelectedTts = "Kokoro";
        config.ApplyCommand.Execute(null);

        Assert.Equal("sk_keep", services.Configuration["Voxa:OpenAI:ApiKey"]);
    }

    [Fact] // review fix (P2) — a Settings save during a live run defers the container rebuild until it stops
    public async Task A_Settings_Save_During_A_Live_Run_Applies_Only_After_The_Run_Stops()
    {
        await using var services = TestSupport.Services();
        var shell = new MainWindowViewModel(services);

        shell.Builder.IsRunning = true;   // a live run still owns a scope from the current container

        var settings = new SettingsViewModel(services.Secrets);
        settings.Providers.AddProvider("ElevenLabs");
        settings.Providers.Rows.Single(r => r.Manifest.Name == "ElevenLabs").Fields.Single().Value = "sk_live";
        settings.Save();
        shell.OnSettingsSaved();

        Assert.Null(services.Configuration["Voxa:ElevenLabs:ApiKey"]);   // deferred — container not torn down mid-run

        shell.Builder.IsRunning = false;   // run stops → the pending apply fires
        Assert.Equal("sk_live", services.Configuration["Voxa:ElevenLabs:ApiKey"]);
    }

    [Fact] // review fix (P2) — saving a missing key re-validates Config so Apply re-enables without an extra edit
    public async Task Saving_A_Key_Revalidates_The_Config_Draft()
    {
        var config = TestSupport.LocalConfig(null,
            ("Voxa:Agent:Provider", "OpenAI"), ("Voxa:Agent:Model", "gpt-4o-mini"));
        await using var services = new StudioServices(config, new NullAudioDevice(),
            new MemorySecretsStore(), new ProviderActivationStore(TestSupport.TempActivationsPath()),
            new PipelineProfileStore(TestSupport.TempProfilesPath()));
        var shell = new MainWindowViewModel(services);

        Assert.False(shell.Config.IsValid);   // OpenAI agent selected, no key → invalid

        var settings = new SettingsViewModel(services.Secrets);
        settings.Providers.AddProvider("OpenAI");
        settings.Providers.Rows.Single(r => r.Manifest.Name == "OpenAI").Fields.Single().Value = "sk-123";
        settings.Save();
        shell.OnSettingsSaved();

        Assert.True(shell.Config.IsValid);    // re-validated against the new key; Apply is enabled again
    }
}
