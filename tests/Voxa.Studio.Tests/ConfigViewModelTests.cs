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
    public void Dropdowns_Show_Local_Providers_And_Hide_Unactivated_Cloud_Providers()
    {
        // VST-003: the dropdowns are the live registry filtered to activated-or-local identities.
        // With nothing activated, only the local tier shows — cloud providers stay hidden until the
        // user adds them in Settings.
        var vm = Vm();
        Assert.Contains("WhisperCpp", vm.SttProviders);     // local STT
        Assert.Contains("Piper", vm.TtsProviders);          // local TTS
        Assert.Contains("Kokoro", vm.TtsProviders);         // local TTS
        Assert.DoesNotContain("OpenAI", vm.SttProviders);   // cloud, not activated
        Assert.DoesNotContain("ElevenLabs", vm.TtsProviders); // cloud, not activated
    }

    [Fact]
    public void Activating_A_Cloud_Provider_Adds_It_To_The_Dropdowns_On_Refresh()
    {
        var services = TestSupport.Services();
        var vm = new ConfigViewModel(services);
        Assert.DoesNotContain("ElevenLabs", vm.TtsProviders);

        services.Secrets.Activate("ElevenLabs");
        vm.RefreshProviderLists();

        Assert.Contains("ElevenLabs", vm.TtsProviders);     // now offered
        Assert.Contains("Piper", vm.TtsProviders);          // locals still present
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

    // ── LLM agent configuration (talk to a real model) ──────────────────────

    [Fact]
    public void OpenAI_Agent_Without_A_Key_Is_Invalid_With_Actionable_Guidance()
    {
        var vm = Vm();
        vm.SelectedAgent = "OpenAI";

        // The agent factory's own credential check runs at draft time, so the user learns
        // about the missing key in the Config tab — not when the Talk session fails to start.
        Assert.False(vm.IsValid);
        Assert.Contains(vm.ValidationErrors, e => e.Contains("ApiKey", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OpenAI_Agent_With_A_Key_Validates_And_The_Key_Never_Reaches_The_Export()
    {
        var vm = Vm();
        vm.SelectedAgent = "OpenAI";
        vm.AgentModel = "gpt-4o-mini";
        vm.AgentApiKey = "sk-test-secret";

        Assert.True(vm.IsValid, string.Join("; ", vm.ValidationErrors));

        // Export carries the provider + model, NEVER the secret.
        Assert.Contains("\"OpenAI\"", vm.ExportJson);
        Assert.Contains("gpt-4o-mini", vm.ExportJson);
        Assert.DoesNotContain("sk-test-secret", vm.ExportJson);

        // The apply path (and only the apply path) carries it.
        Assert.Equal("sk-test-secret", vm.DraftPairs(includeSecrets: true)["Voxa:Agent:ApiKey"]);
        Assert.False(vm.DraftPairs(includeSecrets: false).ContainsKey("Voxa:Agent:ApiKey"));
    }

    [Fact]
    public void Apply_Reconfigures_The_Live_Container()
    {
        var services = TestSupport.Services();
        var vm = new ConfigViewModel(services);
        vm.SelectedAgent = "OpenAI";
        vm.AgentApiKey = "sk-test-secret";
        vm.SelectedTts = "Kokoro";

        Assert.True(vm.ApplyCommand.CanExecute(null));
        vm.ApplyCommand.Execute(null);

        // The LIVE configuration — what the next Talk session composes from — now has the draft.
        Assert.Equal("Kokoro", services.Configuration["Voxa:Tts"]);
        Assert.Equal("OpenAI", services.Configuration["Voxa:Agent:Provider"]);
        Assert.Equal("sk-test-secret", services.Configuration["Voxa:Agent:ApiKey"]);
        Assert.NotNull(vm.ApplyStatus);
        Assert.Contains("Applied", vm.ApplyStatus);
    }

    [Fact]
    public void Apply_Is_Blocked_While_A_Talk_Session_Is_Live_Or_Draft_Is_Invalid()
    {
        var vm = Vm();
        Assert.True(vm.ApplyCommand.CanExecute(null));

        vm.ApplyBlocked = true; // MainWindowViewModel sets this from Talk.IsRunning
        Assert.False(vm.ApplyCommand.CanExecute(null));

        vm.ApplyBlocked = false;
        vm.SelectedAgent = "OpenAI"; // no key anywhere → invalid draft
        Assert.False(vm.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public void Apply_Propagates_To_The_Talk_Header_Through_The_Shell()
    {
        var services = TestSupport.Services();
        var shell = new Voxa.Studio.ViewModels.MainWindowViewModel(services);

        shell.Config.SelectedAgent = "OpenAI";
        shell.Config.AgentModel = "gpt-4o";
        shell.Config.AgentApiKey = "sk-test-secret";
        shell.Config.ApplyCommand.Execute(null);

        Assert.Contains("OpenAI / gpt-4o", shell.Talk.ProviderChain);
    }
}
