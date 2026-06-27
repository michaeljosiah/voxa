using Microsoft.Extensions.Configuration;
using Voxa.Speech.Voxtral;
using Voxa.Studio.Audio;
using Voxa.Studio.Services;

namespace Voxa.Studio.Tests;

/// <summary>VLS-009 §6: the GPU-gated default-STT precedence, pure and wired through StudioServices.</summary>
public class DefaultSttSelectorTests
{
    private sealed class FakeGpu(int gb) : IGpuInfoProbe
    {
        public int LargestGpuMemoryGb() => gb;
    }

    [Theory]
    [InlineData("WhisperCpp", true, true, "WhisperCpp")]  // an explicit choice always wins…
    [InlineData("Deepgram", true, true, "Deepgram")]      // …whatever it is
    [InlineData(null, true, true, "Voxtral")]             // capable + configured → Voxtral
    [InlineData("", true, true, "Voxtral")]               // blank counts as unset
    [InlineData(null, false, true, "WhisperCpp")]         // capable but no server configured
    [InlineData(null, true, false, "WhisperCpp")]         // server configured but GPU too small
    [InlineData(null, false, false, "WhisperCpp")]        // fresh GPU-less install — behaves as before
    public void Select_Applies_The_Precedence(string? explicitStt, bool configured, bool capable, string expected)
        => Assert.Equal(expected, DefaultSttSelector.Select(explicitStt, configured, capable));

    [Fact]
    public async Task Wiring_Capable_Gpu_And_Configured_Server_Defaults_To_Voxtral()
    {
        await using var services = Build(new FakeGpu(24), voxtralServer: "ws://127.0.0.1:8000", explicitStt: null);
        Assert.Equal("Voxtral", services.Configuration["Voxa:Stt"]);
    }

    [Fact]
    public async Task Wiring_Incapable_Gpu_Falls_Back_To_WhisperCpp()
    {
        await using var services = Build(new FakeGpu(8), voxtralServer: "ws://127.0.0.1:8000", explicitStt: null);
        Assert.Equal("WhisperCpp", services.Configuration["Voxa:Stt"]);
    }

    [Fact]
    public async Task Wiring_No_Server_Configured_Falls_Back_To_WhisperCpp()
    {
        await using var services = Build(new FakeGpu(24), voxtralServer: null, explicitStt: null);
        Assert.Equal("WhisperCpp", services.Configuration["Voxa:Stt"]);
    }

    [Fact]
    public async Task Wiring_Explicit_Stt_Beats_The_Gate()
    {
        await using var services = Build(new FakeGpu(24), voxtralServer: "ws://127.0.0.1:8000", explicitStt: "WhisperCpp");
        Assert.Equal("WhisperCpp", services.Configuration["Voxa:Stt"]);
    }

    private static StudioServices Build(IGpuInfoProbe gpu, string? voxtralServer, string? explicitStt)
    {
        var pairs = new Dictionary<string, string?>
        {
            ["Voxa:Tts"] = "Piper",
            ["Voxa:Agent:Provider"] = "Echo",
            ["Voxa:Models:CachePath"] = TestSupport.TempDir(),
        };
        if (voxtralServer is not null) pairs["Voxa:Voxtral:ServerUrl"] = voxtralServer;
        if (explicitStt is not null) pairs["Voxa:Stt"] = explicitStt;

        var config = new ConfigurationBuilder().AddInMemoryCollection(pairs).Build();
        return new StudioServices(
            config,
            new NullAudioDevice(),
            new MemorySecretsStore(),
            new ProviderActivationStore(TestSupport.TempActivationsPath()),
            new PipelineProfileStore(TestSupport.TempProfilesPath()),
            gpu);
    }
}
