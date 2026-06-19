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

    // ── Ollama (local, keyless LLM agent — VLS-003 surfaced in Studio) ───────

    [Fact]
    public void Ollama_Is_Offered_As_An_Agent_Provider()
        => Assert.Contains("Ollama", Vm().AgentProviders);

    [Fact]
    public void Switching_To_Ollama_Validates_Keylessly_And_Swaps_The_Model_Placeholder()
    {
        var vm = Vm();
        vm.SelectedAgent = "Ollama";

        // The model field swaps from the OpenAI default to a sensible local one, and the card shows the
        // Base URL row (Ollama) instead of the API-key row (OpenAI).
        Assert.Equal("llama3.2", vm.AgentModel);
        Assert.True(vm.ShowAgentOptions);
        Assert.True(vm.ShowAgentBaseUrl);
        Assert.False(vm.ShowAgentApiKey);

        // Keyless: valid out of the box against the default local daemon endpoint.
        Assert.True(vm.IsValid, string.Join("; ", vm.ValidationErrors));
        Assert.Contains("\"Ollama\"", vm.ExportJson);
        Assert.Contains("llama3.2", vm.ExportJson);
        Assert.DoesNotContain("ApiKey", vm.ExportJson);

        // The default base URL stays implicit; no secret is ever drafted for Ollama.
        var pairs = vm.DraftPairs(includeSecrets: true);
        Assert.Equal("Ollama", pairs["Voxa:Agent:Provider"]);
        Assert.False(pairs.ContainsKey("Voxa:Agent:BaseUrl"));
        Assert.False(pairs.ContainsKey("Voxa:Agent:ApiKey"));
    }

    [Fact]
    public void Ollama_Custom_Base_Url_Reaches_The_Export_And_Default_Stays_Implicit()
    {
        var vm = Vm();
        vm.SelectedAgent = "Ollama";

        Assert.DoesNotContain("BaseUrl", vm.ExportJson); // default endpoint is implicit

        vm.AgentBaseUrl = "http://gpu-box:11434/v1";
        Assert.True(vm.IsValid, string.Join("; ", vm.ValidationErrors));
        Assert.Contains("http://gpu-box:11434/v1", vm.ExportJson);
        Assert.Equal("http://gpu-box:11434/v1", vm.DraftPairs()["Voxa:Agent:BaseUrl"]);
    }

    [Fact]
    public void Switching_Back_To_OpenAI_Restores_The_Cloud_Model_Placeholder()
    {
        var vm = Vm();
        vm.SelectedAgent = "Ollama";
        Assert.Equal("llama3.2", vm.AgentModel);

        vm.SelectedAgent = "OpenAI";
        Assert.Equal("gpt-4o-mini", vm.AgentModel);
        Assert.True(vm.ShowAgentApiKey);
        Assert.False(vm.ShowAgentBaseUrl);
    }

    // ── Whisper GPU device (VLS-002 surfaced in Studio) ──────────────────────

    [Fact]
    public void Whisper_Devices_List_Includes_Cpu_And_Cuda()
    {
        var vm = Vm();
        Assert.Contains("cpu", vm.WhisperDevices);
        Assert.Contains("cuda", vm.WhisperDevices);
    }

    [Fact]
    public void Whisper_Device_Stays_Implicit_On_Cpu_And_Reaches_The_Export_On_Gpu()
    {
        var vm = Vm(); // default STT is WhisperCpp
        Assert.Equal("cpu", vm.SelectedWhisperDevice);
        Assert.False(vm.DraftPairs().ContainsKey("Voxa:WhisperCpp:Device")); // cpu is the implicit default
        Assert.DoesNotContain("Device", vm.ExportJson);

        vm.SelectedWhisperDevice = "cuda";
        Assert.True(vm.IsValid, string.Join("; ", vm.ValidationErrors));
        Assert.Equal("cuda", vm.DraftPairs()["Voxa:WhisperCpp:Device"]);
        Assert.Contains("\"cuda\"", vm.ExportJson);
    }
}
