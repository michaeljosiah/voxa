using Voxa.Studio.ViewModels;

namespace Voxa.Studio.Tests;

/// <summary>
/// VST-001 WS5-A1/A2: every local provider combination the Config tab can compose validates
/// through the real AddVoxa + options-validation path, and the default selection's export is
/// byte-stable JSON matching the documented quickstart shape.
/// </summary>
public class ConfigViewModelTests
{
    private static ConfigViewModel Vm() => new(TestSupport.Services());

    [Fact]
    public void Default_Selection_Is_Valid_And_Seeded_From_The_Running_Config()
    {
        var vm = Vm();
        Assert.Equal("WhisperCpp", vm.SelectedStt);
        Assert.Equal("Piper", vm.SelectedTts);
        Assert.Equal("Echo", vm.SelectedAgent);
        Assert.True(vm.IsValid);
        Assert.Empty(vm.ValidationErrors);
    }

    [Fact]
    public void Every_Local_Combination_Validates()
    {
        var vm = Vm();
        foreach (var tts in new[] { "Piper", "Kokoro" })
        foreach (var vad in new[] { "Silero", "SilenceGate", "None" })
        foreach (var profile in vm.Profiles)
        {
            vm.SelectedTts = tts;
            vm.SelectedVad = vad;
            vm.SelectedProfile = profile;
            Assert.True(vm.IsValid,
                $"{tts}/{vad}/{profile} should be valid but got: {string.Join("; ", vm.ValidationErrors)}");
        }
    }

    [Fact]
    public void Registry_Populates_The_Dropdowns()
    {
        var vm = Vm();
        // The live registry — not a hardcoded list — feeds the choices.
        Assert.Contains("WhisperCpp", vm.SttProviders);
        Assert.Contains("OpenAI", vm.SttProviders);
        Assert.Contains("Piper", vm.TtsProviders);
        Assert.Contains("Kokoro", vm.TtsProviders);
        Assert.Contains("ElevenLabs", vm.TtsProviders);
    }

    [Fact]
    public void Default_Export_Is_Byte_Stable()
    {
        var vm = Vm();
        var expected = """
        {
          "Voxa": {
            "Agent": {
              "Provider": "Echo"
            },
            "Piper": {
              "Voice": "en_US-amy-low"
            },
            "Stt": "WhisperCpp",
            "Tts": "Piper",
            "WhisperCpp": {
              "Model": "tiny.en"
            }
          }
        }
        """.ReplaceLineEndings();

        Assert.Equal(expected, vm.ExportJson.ReplaceLineEndings());
    }

    [Fact]
    public void Switching_To_Kokoro_Swaps_The_Provider_Block()
    {
        var vm = Vm();
        vm.SelectedTts = "Kokoro";

        Assert.True(vm.ShowKokoroOptions);
        Assert.False(vm.ShowPiperOptions);
        Assert.Contains("\"Kokoro\"", vm.ExportJson);
        Assert.Contains("af_heart", vm.ExportJson);
        Assert.DoesNotContain("Piper", vm.ExportJson);
        Assert.True(vm.IsValid);
    }
}
