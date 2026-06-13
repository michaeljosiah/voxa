using Voxa.Speech.Voices;
using Voxa.Studio.Services;

namespace Voxa.Studio.Tests;

/// <summary>
/// VVL-001 WS4: the voice library — a key-free on-disk store, and the reconciliation service that
/// merges a provider's live voices with saved profiles into Live/Stale/Discovered rows and degrades
/// cleanly when a provider has no key.
/// </summary>
public class VoiceLibraryTests
{
    private static VoiceProfile Profile(string name, string provider, string voiceId) => new()
    {
        DisplayName = name,
        ProviderName = provider,
        ProviderVoiceId = voiceId,
        Kind = VoiceKind.Cloned,
        ConsentAttestedAt = DateTimeOffset.Now,
    };

    private sealed class FakeCatalog(params ProviderVoice[] voices) : IVoiceCatalogProvider
    {
        public Task<IReadOnlyList<ProviderVoice>> ListVoicesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ProviderVoice>>(voices);
    }

    [Fact] // WS4-A1
    public void Store_Round_Trips_A_Profile_And_Its_Samples_With_No_Secret_On_Disk()
    {
        var dir = TestSupport.TempDir();
        var store = new VoiceStore(dir);

        var saved = store.Save(
            Profile("My Voice", "ElevenLabs", "voice-123") with { Notes = "test" },
            [new VoiceSample("ref.wav", new byte[] { 1, 2, 3, 4 })]);

        // Samples persisted under the profile dir; the profile points at them.
        Assert.Single(saved.SamplePaths);
        Assert.True(File.Exists(saved.SamplePaths[0]));

        var loaded = Assert.Single(store.Load());
        Assert.Equal("My Voice", loaded.DisplayName);
        Assert.Equal("voice-123", loaded.ProviderVoiceId);
        Assert.NotNull(loaded.ConsentAttestedAt);

        // No API key anywhere in the stored tree.
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            if (!file.EndsWith(".json")) continue;
            Assert.DoesNotContain("ApiKey", File.ReadAllText(file), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact] // WS4-A1
    public void Store_Skips_A_Corrupt_Profile_Instead_Of_Throwing()
    {
        var dir = TestSupport.TempDir();
        var store = new VoiceStore(dir);
        store.Save(Profile("Good", "Mistral", "v1"));
        File.WriteAllText(Path.Combine(dir, "broken.json"), "{ not valid json");

        var loaded = store.Load();   // must not throw

        Assert.Single(loaded);
        Assert.Equal("Good", loaded[0].DisplayName);
    }

    [Fact] // WS4-A1
    public void Delete_Removes_The_Profile_And_Its_Samples()
    {
        var dir = TestSupport.TempDir();
        var store = new VoiceStore(dir);
        var saved = store.Save(Profile("X", "ElevenLabs", "v"), [new VoiceSample("a.wav", new byte[] { 9 })]);

        store.Delete(saved);

        Assert.Empty(store.Load());
        Assert.False(Directory.Exists(Path.Combine(dir, saved.Id)));
    }

    [Fact] // WS4-A2
    public async Task Reconciliation_Tags_Live_Stale_And_Discovered()
    {
        var store = new VoiceStore(TestSupport.TempDir());
        store.Save(Profile("Saved & Live", "ElevenLabs", "live-1"));   // matches a live voice
        store.Save(Profile("Gone", "ElevenLabs", "deleted-1"));        // no longer on the provider

        await using var services = TestSupport.Services();
        var svc = new VoiceCatalogService(services, store)
        {
            CatalogOverride = name => name == "ElevenLabs"
                ? new FakeCatalog(
                    new ProviderVoice("live-1", "Saved & Live", "ElevenLabs", VoiceKind.Cloned),
                    new ProviderVoice("stock-1", "Rachel", "ElevenLabs", VoiceKind.Standard))
                : null,
        };

        var set = await svc.ForProviderAsync("ElevenLabs", default);

        Assert.False(set.MissingKey);
        var byId = set.Voices.ToDictionary(v => v.Voice.Id);
        Assert.Equal(VoiceState.Live, byId["live-1"].State);
        Assert.NotNull(byId["live-1"].Profile);
        Assert.Equal(VoiceState.Discovered, byId["stock-1"].State);   // live, no saved profile
        Assert.Equal(VoiceState.Stale, byId["deleted-1"].State);      // saved, not live
    }

    [Fact] // WS4-A3
    public async Task A_Provider_With_No_Key_Degrades_To_Stale_Profiles_Not_A_Crash()
    {
        var store = new VoiceStore(TestSupport.TempDir());
        store.Save(Profile("My ElevenLabs Clone", "ElevenLabs", "voice-x"));

        await using var services = TestSupport.Services();   // keyless config — no ElevenLabs key
        var svc = new VoiceCatalogService(services, store);

        var set = await svc.ForProviderAsync("ElevenLabs", default);

        Assert.True(set.MissingKey);
        var row = Assert.Single(set.Voices);
        Assert.Equal(VoiceState.Stale, row.State);   // unverifiable without a key
    }

    [Fact] // WS4-A2 (local catalog providers surface as LocalCatalog)
    public async Task Local_Catalog_Providers_Surface_As_LocalCatalog()
    {
        await using var services = TestSupport.Services();
        var svc = new VoiceCatalogService(services, new VoiceStore(TestSupport.TempDir()));

        var set = await svc.ForProviderAsync("Piper", default);

        Assert.NotEmpty(set.Voices);
        Assert.All(set.Voices, v => Assert.Equal(VoiceState.LocalCatalog, v.State));
    }
}
