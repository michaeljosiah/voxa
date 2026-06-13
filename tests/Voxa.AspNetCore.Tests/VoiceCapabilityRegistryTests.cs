using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Voxa.Speech;
using Voxa.Speech.Voices;

namespace Voxa.AspNetCore.Tests;

/// <summary>
/// VVL-001 WS0: the voice capability seams resolve through the registry only when a descriptor
/// opts in, and they honour the config-capture rule (no service-located IConfiguration) so they
/// work on a plain ServiceCollection host like Studio — the VST-001 DefaultAgentFactory bug class.
/// </summary>
public class VoiceCapabilityRegistryTests
{
    // A descriptor with no resolvers — the Piper/Kokoro shape (its voices are a compiled-in list).
    private static VoxaTtsDescriptor PlainTts(string name = "Piper") => new(
        Name: name, ConfigSection: name, OutputSampleRate: 22050,
        Validate: _ => [],
        CreateProcessor: (_, _) => throw new NotSupportedException("not exercised"));

    private sealed class FakeCatalog(string note) : IVoiceCatalogProvider
    {
        public string Note { get; } = note;
        public Task<IReadOnlyList<ProviderVoice>> ListVoicesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ProviderVoice>>([]);
    }

    [Fact] // WS0-A1
    public void A_Descriptor_With_No_Resolvers_Exposes_No_Capability()
    {
        var registry = new VoxaProviderRegistry();
        registry.Add(PlainTts());

        var sp = new ServiceCollection().BuildServiceProvider();
        var root = new ConfigurationBuilder().Build().GetSection("Voxa");

        Assert.False(registry.TryGetVoiceCatalog("Piper", sp, root, out _));
        Assert.False(registry.TryGetVoiceCloner("Piper", sp, root, out _));
    }

    [Fact] // WS0-A1
    public void A_Descriptor_With_A_Catalog_Resolver_Returns_A_Live_Instance()
    {
        var descriptor = PlainTts("ElevenLabs") with
        {
            ResolveCatalog = (_, _) => new FakeCatalog("resolved"),
        };
        var registry = new VoxaProviderRegistry();
        registry.Add(descriptor);

        var sp = new ServiceCollection().BuildServiceProvider();
        var root = new ConfigurationBuilder().Build().GetSection("Voxa");

        Assert.True(registry.TryGetVoiceCatalog("ElevenLabs", sp, root, out var catalog));
        Assert.Equal("resolved", Assert.IsType<FakeCatalog>(catalog).Note);
        // Cloner still absent — capabilities are independent.
        Assert.False(registry.TryGetVoiceCloner("ElevenLabs", sp, root, out _));
    }

    [Fact] // WS0-A1
    public void An_Unknown_Provider_Name_Resolves_No_Capability()
    {
        var registry = new VoxaProviderRegistry();
        var sp = new ServiceCollection().BuildServiceProvider();
        var root = new ConfigurationBuilder().Build().GetSection("Voxa");

        Assert.False(registry.TryGetVoiceCatalog("Nope", sp, root, out _));
    }

    [Fact] // WS0-A2 — the config-capture guarantee
    public void Resolving_A_Capability_Reads_The_Passed_Root_Not_Service_Located_Config()
    {
        // The resolver reads its key from the SUPPLIED section. The DI container deliberately has
        // NO IConfiguration registered (a plain ServiceCollection, exactly like Studio/tests); if
        // the registry or resolver tried to service-locate it, this would throw.
        string? seenKey = null;
        var descriptor = PlainTts("ElevenLabs") with
        {
            ResolveCatalog = (sp, voxaRoot) =>
            {
                Assert.Null(sp.GetService(typeof(IConfiguration))); // no implicit IConfiguration here
                seenKey = voxaRoot["ElevenLabs:ApiKey"];
                return new FakeCatalog(seenKey ?? "");
            },
        };
        var registry = new VoxaProviderRegistry();
        registry.Add(descriptor);

        var sp = new ServiceCollection().BuildServiceProvider();
        var root = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Voxa:ElevenLabs:ApiKey"] = "sk-test" })
            .Build()
            .GetSection("Voxa");

        Assert.True(registry.TryGetVoiceCatalog("ElevenLabs", sp, root, out _));
        Assert.Equal("sk-test", seenKey);
    }
}
