using Voxa.Studio.Services;

namespace Voxa.Studio.Tests;

/// <summary>VST-003 WS1: the secrets + activation stores round-trip and survive corrupt files.</summary>
public class SecretsStoreTests
{
    [Fact] // WS1-A1 — DPAPI round-trip across instances (Windows lane only)
    public void Dpapi_Store_Round_Trips_Across_Instances()
    {
        if (!OperatingSystem.IsWindows()) return;   // ProtectedData is Windows-only
        var path = Path.Combine(TestSupport.TempDir(), "secrets.dpapi");

        var a = new DpapiSecretsStore(path);
        a.Set("OpenAI:ApiKey", "sk-123");
        a.Save();

        var b = new DpapiSecretsStore(path);
        Assert.Equal("sk-123", b.Get("OpenAI:ApiKey"));
    }

    [Fact] // WS1-A2 — an undecryptable blob is treated as empty, never thrown
    public void Dpapi_Store_Treats_A_Corrupt_File_As_Empty()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(TestSupport.TempDir(), "secrets.dpapi");
        File.WriteAllBytes(path, [1, 2, 3, 4, 5]);   // not a valid DPAPI blob

        var store = new DpapiSecretsStore(path);     // must not throw
        Assert.Empty(store.Keys);
    }

    [Fact] // WS1-A3
    public void Memory_Store_Round_Trips_And_Reload_Restores_The_Snapshot()
    {
        var store = new MemorySecretsStore();
        store.Set("ElevenLabs:ApiKey", "k");
        store.Save();                                 // no-op on disk, snapshots in memory

        store.Set("ElevenLabs:ApiKey", "changed");
        store.Reload();                               // drops the edit
        Assert.Equal("k", store.Get("ElevenLabs:ApiKey"));
    }

    [Fact] // WS1-A4
    public void Activation_Store_Round_Trips_And_Skips_A_Corrupt_File()
    {
        var path = Path.Combine(TestSupport.TempDir(), "acts.json");
        new ProviderActivationStore(path).Save([new ProviderActivation("ElevenLabs", DateTimeOffset.UtcNow)]);

        var reloaded = new ProviderActivationStore(path).Load();
        Assert.Equal("ElevenLabs", Assert.Single(reloaded).Name);

        File.WriteAllText(path, "{ not json");
        Assert.Empty(new ProviderActivationStore(path).Load());   // corrupt → empty, no throw
    }
}
