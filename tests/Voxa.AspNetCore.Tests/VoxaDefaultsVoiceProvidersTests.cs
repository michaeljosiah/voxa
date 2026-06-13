using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Voxa;
using Voxa.Speech.Voices;

namespace Voxa.AspNetCore.Tests;

/// <summary>
/// VVL-001 WS2/WS1: the meta-package registers Voxtral STT alongside Mistral TTS, and the cloud TTS
/// providers expose the voice-catalog/clone capability through the registry.
/// </summary>
public class VoxaDefaultsVoiceProvidersTests
{
    private static VoxaProviderRegistry RegistryFromMetaPackage()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddVoxa(config);   // the meta-package overload — registers every built-in provider
        return services.BuildServiceProvider().GetRequiredService<VoxaProviderRegistry>();
    }

    [Fact] // WS2-A3
    public void Meta_Package_Registers_Voxtral_Stt_And_Mistral_Tts()
    {
        var registry = RegistryFromMetaPackage();
        Assert.Contains("Mistral", registry.SttNames, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Mistral", registry.TtsNames, StringComparer.OrdinalIgnoreCase);
    }

    [Theory] // WS1/WS2: cloud TTS providers expose catalog + clone; local ones do not
    [InlineData("ElevenLabs")]
    [InlineData("Mistral")]
    public void Cloud_Tts_Providers_Expose_Voice_Catalog_And_Clone(string provider)
    {
        var registry = RegistryFromMetaPackage();
        var sp = new ServiceCollection().BuildServiceProvider();
        var root = new ConfigurationBuilder().Build().GetSection("Voxa");

        Assert.True(registry.TryGetVoiceCatalog(provider, sp, root, out var catalog));
        Assert.IsAssignableFrom<IVoiceCatalogProvider>(catalog);
        Assert.True(registry.TryGetVoiceCloner(provider, sp, root, out var cloner));
        Assert.IsAssignableFrom<IVoiceCloneProvider>(cloner);
    }

    [Theory] // Piper/Kokoro have compiled-in catalogs — no live capability
    [InlineData("Piper")]
    [InlineData("Kokoro")]
    public void Local_Tts_Providers_Expose_No_Live_Capability(string provider)
    {
        var registry = RegistryFromMetaPackage();
        var sp = new ServiceCollection().BuildServiceProvider();
        var root = new ConfigurationBuilder().Build().GetSection("Voxa");

        Assert.False(registry.TryGetVoiceCatalog(provider, sp, root, out _));
        Assert.False(registry.TryGetVoiceCloner(provider, sp, root, out _));
    }
}
