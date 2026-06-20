using Voxa.Studio.Services;
using Voxa.Studio.ViewModels;

namespace Voxa.Studio.Tests;

/// <summary>
/// Smart turn (a VAD modifier) and the pre-VAD input cleanup (AEC/denoise) round-trip through the Builder:
/// seeding from config lands them on the VAD / Source nodes, and the appsettings export emits them back —
/// so "Open in Builder" and a saved profile preserve them rather than silently dropping them.
/// </summary>
public class BuilderInputCleanupRoundTripTests
{
    private static Dictionary<string, string?> SeedPairs() => new()
    {
        ["Voxa:Vad:Engine"] = "Silero",
        ["Voxa:Aec:Engine"] = "WebRtc",
        ["Voxa:Enhance:Engine"] = "DeepFilterNet3",
        ["Voxa:SmartTurn:Provider"] = "Sidecar",
        ["Voxa:SmartTurn:PythonScript"] = "sidecar/x.py",
        ["Voxa:Stt"] = "WhisperCpp",
        ["Voxa:Agent:Provider"] = "Echo",
        ["Voxa:Tts"] = "Piper",
    };

    [Fact]
    public void Seed_Lands_Cleanup_On_The_Mic_Node_And_SmartTurn_On_The_Vad()
    {
        var vm = new BuilderViewModel(TestSupport.Services());
        vm.SeedFromPairs(SeedPairs());

        var source = vm.Nodes.Single(n => n.Kind == BuilderNodeKind.Source);
        Assert.Equal("WebRtc", source.Model.Options["AecEngine"]);
        Assert.Equal("DeepFilterNet3", source.Model.Options["EnhanceEngine"]);
        Assert.Contains("AEC WebRtc", source.Meta);       // shows on the node card
        Assert.Contains("denoise DeepFilterNet3", source.Meta);

        var vad = vm.Nodes.Single(n => n.Kind == BuilderNodeKind.Vad);
        Assert.Equal("Sidecar", vad.Model.Options["SmartTurnProvider"]);
        Assert.Equal("sidecar/x.py", vad.Model.Options["SmartTurnPythonScript"]);
        Assert.Contains("smart turn", vad.Meta);
    }

    [Fact]
    public void Export_Round_Trips_Cleanup_And_SmartTurn()
    {
        var vm = new BuilderViewModel(TestSupport.Services());
        vm.SeedFromPairs(SeedPairs());
        Assert.True(vm.IsDefaultShape); // still the default chain — exports as appsettings

        vm.ExportAppSettingsCommand.Execute(null);

        Assert.Contains("WebRtc", vm.ExportText);          // Voxa:Aec:Engine
        Assert.Contains("DeepFilterNet3", vm.ExportText);  // Voxa:Enhance:Engine
        Assert.Contains("SmartTurn", vm.ExportText);       // Voxa:SmartTurn block
        Assert.Contains("Sidecar", vm.ExportText);
    }

    [Fact]
    public void Absent_Keys_Stay_Absent_The_Default_Chain_Is_Unchanged()
    {
        // A plain local config (no AEC/denoise/smart-turn) must not sprout empty options or badges.
        var vm = new BuilderViewModel(TestSupport.Services());

        var source = vm.Nodes.Single(n => n.Kind == BuilderNodeKind.Source);
        Assert.False(source.Model.Options.ContainsKey("AecEngine"));
        Assert.False(source.Model.Options.ContainsKey("EnhanceEngine"));

        var vad = vm.Nodes.Single(n => n.Kind == BuilderNodeKind.Vad);
        Assert.False(vad.Model.Options.ContainsKey("SmartTurnProvider"));
        Assert.DoesNotContain("smart turn", vad.Meta);
    }

    [Fact] // codex round 2: an explicit "None" override must survive Open-in-Builder → export, not be dropped.
    public void Explicit_None_Override_Is_Preserved_But_Not_Badged()
    {
        // Config emits Voxa:Aec/Enhance:Engine = "None" (and SmartTurn:Provider = "None") to turn OFF a
        // base-config engine. The Builder must carry that explicit off through to the export, or the layered
        // base config would silently re-enable the stage on run/save.
        var vm = new BuilderViewModel(TestSupport.Services());
        vm.SeedFromPairs(new Dictionary<string, string?>
        {
            ["Voxa:Vad:Engine"] = "Silero",
            ["Voxa:Aec:Engine"] = "None",
            ["Voxa:Enhance:Engine"] = "None",
            ["Voxa:SmartTurn:Provider"] = "None",
            ["Voxa:Stt"] = "WhisperCpp",
            ["Voxa:Agent:Provider"] = "Echo",
            ["Voxa:Tts"] = "Piper",
        });

        // Preserved on the nodes…
        var source = vm.Nodes.Single(n => n.Kind == BuilderNodeKind.Source);
        Assert.Equal("None", source.Model.Options["AecEngine"]);
        Assert.Equal("None", source.Model.Options["EnhanceEngine"]);
        var vad = vm.Nodes.Single(n => n.Kind == BuilderNodeKind.Vad);
        Assert.Equal("None", vad.Model.Options["SmartTurnProvider"]);

        // …but an "off" stage is not badged (only real engines show on the card).
        Assert.DoesNotContain("AEC", source.Meta);
        Assert.DoesNotContain("denoise", source.Meta);
        Assert.DoesNotContain("smart turn", vad.Meta);

        // …and the export emits the explicit off so a base engine can't re-enable.
        Assert.True(vm.IsDefaultShape);
        vm.ExportAppSettingsCommand.Execute(null);
        Assert.Contains("\"Aec\"", vm.ExportText);
        Assert.Contains("\"Enhance\"", vm.ExportText);
        Assert.Contains("\"SmartTurn\"", vm.ExportText);
    }
}
