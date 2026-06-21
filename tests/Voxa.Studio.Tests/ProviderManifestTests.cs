using Voxa.Studio.Services;

namespace Voxa.Studio.Tests;

/// <summary>VST-003 WS2: the manifest catalog is well-formed and stays in lock-step with the registry.</summary>
public class ProviderManifestTests
{
    [Fact] // WS2-A1 — identities are distinct (count grows as providers are added; don't pin it)
    public void Catalog_Identities_Are_Distinct()
    {
        var names = ProviderManifestCatalog.All.Select(m => m.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        // sanity: the #71 streaming/batch STT vendors are catalogued so Config gates them by activation.
        Assert.Contains("Deepgram", names);
        Assert.Contains("Aws", names);
    }

    [Fact] // WS2-A2
    public void Every_Cloud_Identity_Has_A_Secret_Field()
    {
        foreach (var manifest in ProviderManifestCatalog.All.Where(m => !m.IsLocal))
        {
            Assert.NotEmpty(manifest.Fields);
            Assert.Contains(manifest.Fields, f => f.IsSecret);
        }
    }

    [Fact] // WS2-A3 — every cloud identity's roles map to a live registry entry
    public async Task Every_Cloud_Identity_Matches_The_Live_Registry()
    {
        await using var services = TestSupport.Services();
        var stt = services.Registry.SttNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tts = services.Registry.TtsNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var manifest in ProviderManifestCatalog.All.Where(m => !m.IsLocal))
        {
            if (manifest.Roles.Contains(ProviderRole.Stt)) Assert.Contains(manifest.Name, stt);
            if (manifest.Roles.Contains(ProviderRole.Tts)) Assert.Contains(manifest.Name, tts);
        }
    }

    [Fact] // WS2-A4
    public void Local_Identities_Are_Exactly_The_Keyless_Tier_With_No_Fields()
    {
        var locals = ProviderManifestCatalog.All.Where(m => m.IsLocal).Select(m => m.Name).OrderBy(n => n);
        Assert.Equal(["Echo", "Kokoro", "Piper", "WhisperCpp"], locals);
        Assert.All(ProviderManifestCatalog.All.Where(m => m.IsLocal), m => Assert.Empty(m.Fields));
    }

    [Fact] // WS2-A5 — OpenAI is one key for three roles; Azure has two fields, region not secret
    public void OpenAI_Is_One_Key_For_Three_Roles_And_Azure_Has_Two_Fields()
    {
        var openai = ProviderManifestCatalog.Find("OpenAI")!;
        Assert.Equal([ProviderRole.Stt, ProviderRole.Tts, ProviderRole.Agent], openai.Roles);
        Assert.Equal("Voxa:OpenAI:ApiKey", Assert.Single(openai.Fields).ConfigKey);

        var azure = ProviderManifestCatalog.Find("Azure")!;
        Assert.Equal(2, azure.Fields.Count);
        Assert.Contains(azure.Fields, f => f.Name == "Region" && !f.IsSecret);
    }
}
