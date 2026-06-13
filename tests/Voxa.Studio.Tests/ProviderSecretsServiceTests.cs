using Voxa.Studio.Services;

namespace Voxa.Studio.Tests;

/// <summary>VST-003 WS3: the secrets facade + the StudioServices secrets layer (the regression guard
/// for "a Config Apply must not wipe stored keys").</summary>
public class ProviderSecretsServiceTests
{
    private static ProviderSecretsService Service(ISecretsStore? store = null) =>
        new(store ?? new MemorySecretsStore(), new ProviderActivationStore(TestSupport.TempActivationsPath()));

    [Fact] // WS3-A1 — a pre-loaded secret reaches the container at startup, no manual call
    public async Task A_PreLoaded_Secret_Is_Live_At_Startup()
    {
        var secrets = new MemorySecretsStore(new Dictionary<string, string> { ["OpenAI:ApiKey"] = "test-key" });
        await using var services = TestSupport.Services(secrets: secrets);
        Assert.Equal("test-key", services.Configuration["Voxa:OpenAI:ApiKey"]);
    }

    [Fact] // WS3-A2 — a Config Apply replaces overrides but keeps the secrets layer
    public async Task Reconfigure_Does_Not_Wipe_The_Secrets_Layer()
    {
        var secrets = new MemorySecretsStore(new Dictionary<string, string> { ["OpenAI:ApiKey"] = "test-key" });
        await using var services = TestSupport.Services(secrets: secrets);

        services.Reconfigure(new Dictionary<string, string?> { ["Voxa:Tts"] = "Kokoro" });

        Assert.Equal("test-key", services.Configuration["Voxa:OpenAI:ApiKey"]);   // survived the Apply
        Assert.Equal("Kokoro", services.Configuration["Voxa:Tts"]);
    }

    [Fact] // WS3-A3
    public void Deactivate_Removes_The_Activation_And_Clears_Its_Secrets()
    {
        var svc = Service();
        svc.Activate("ElevenLabs");
        svc.SetSecret("ElevenLabs", "ApiKey", "sk_1");
        svc.Save();

        svc.Deactivate("ElevenLabs");
        svc.Save();

        Assert.DoesNotContain(svc.Activated, m => m.Name == "ElevenLabs");
        Assert.Null(svc.GetSecret("ElevenLabs", "ApiKey"));
    }

    [Fact] // WS3-A4 — build pairs never leak a file path or the store location
    public void BuildConfigPairs_Maps_Keys_And_Leaks_No_Path()
    {
        var store = new MemorySecretsStore(new Dictionary<string, string> { ["ElevenLabs:ApiKey"] = "sk_1" });
        var pairs = new ProviderSecretsService(store, new ProviderActivationStore(TestSupport.TempActivationsPath()))
            .BuildConfigPairs();

        Assert.Equal("sk_1", pairs["Voxa:ElevenLabs:ApiKey"]);
        Assert.DoesNotContain(pairs.Keys, k =>
            k.Contains("dpapi") || k.Contains("voxa-secrets") || k.Contains(Path.DirectorySeparatorChar));
    }

    [Fact] // WS3-A5
    public void Activating_A_Local_Provider_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => Service().Activate("Piper"));
    }

    [Fact] // WS3-A6
    public void Discard_Drops_Edits_Since_The_Last_Save()
    {
        var svc = Service();
        svc.SetSecret("Mistral", "ApiKey", "edited");
        svc.Discard();
        Assert.DoesNotContain("Voxa:Mistral:ApiKey", svc.BuildConfigPairs().Keys);
    }
}
