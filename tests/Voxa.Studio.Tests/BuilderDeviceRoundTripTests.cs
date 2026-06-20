using Voxa.Studio.Services;
using Voxa.Studio.ViewModels;

namespace Voxa.Studio.Tests;

/// <summary>
/// The compute device (whisper STT — VLS-002 / Kokoro TTS — VLS-006) round-trips through the Builder: seeding
/// lands it on the STT/TTS node and the appsettings export emits it back, so a GPU selection survives
/// Config → "Open in Builder" → run/export instead of silently reverting to CPU (codex PR #44).
/// </summary>
public class BuilderDeviceRoundTripTests
{
    private static Dictionary<string, string?> SeedPairs() => new()
    {
        ["Voxa:Vad:Engine"] = "Silero",
        ["Voxa:Stt"] = "WhisperCpp",
        ["Voxa:WhisperCpp:Device"] = "vulkan",
        ["Voxa:Agent:Provider"] = "Echo",
        ["Voxa:Tts"] = "Kokoro",
        ["Voxa:Kokoro:Device"] = "cuda",
    };

    [Fact]
    public void Seed_Lands_Device_On_The_Stt_And_Tts_Nodes()
    {
        var vm = new BuilderViewModel(TestSupport.Services());
        vm.SeedFromPairs(SeedPairs());

        var stt = vm.Nodes.Single(n => n.Kind == BuilderNodeKind.Stt);
        Assert.Equal("vulkan", stt.Model.Options["Device"]);

        var tts = vm.Nodes.Single(n => n.Kind == BuilderNodeKind.Tts);
        Assert.Equal("cuda", tts.Model.Options["Device"]);
    }

    [Fact]
    public void Export_Round_Trips_The_Device()
    {
        var vm = new BuilderViewModel(TestSupport.Services());
        vm.SeedFromPairs(SeedPairs());
        Assert.True(vm.IsDefaultShape); // still the default chain — exports as appsettings

        vm.ExportAppSettingsCommand.Execute(null);

        Assert.Contains("vulkan", vm.ExportText);   // Voxa:WhisperCpp:Device
        Assert.Contains("cuda", vm.ExportText);     // Voxa:Kokoro:Device
    }

    [Fact]
    public void Absent_Device_Stays_Absent()
    {
        // A plain local config (no device key) must not sprout a Device option on the STT node — cpu is implicit.
        var vm = new BuilderViewModel(TestSupport.Services());

        var stt = vm.Nodes.Single(n => n.Kind == BuilderNodeKind.Stt);
        Assert.False(stt.Model.Options.ContainsKey("Device"));
    }
}
