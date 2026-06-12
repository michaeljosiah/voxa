using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Voxa.AspNetCore;
using Voxa.Speech.Kokoro;
using Voxa.Speech.Piper;
using Voxa.Speech.WhisperCpp;
using Voxa.Studio.Services;

namespace Voxa.Studio.ViewModels;

/// <summary>
/// The Config composer (VST-001 WS5): build a pipeline selection from the LIVE provider
/// registry (the dropdowns can never drift from the code), validate it through the exact
/// code path a server boots with (<c>AddVoxa</c> + options validation), and export the
/// <c>appsettings.json</c> block.
/// </summary>
public sealed partial class ConfigViewModel : ObservableObject
{
    private readonly StudioServices _services;

    public ConfigViewModel(StudioServices services)
    {
        _services = services;

        SttProviders = services.Registry.SttNames.OrderBy(n => n, StringComparer.Ordinal).ToList();
        TtsProviders = services.Registry.TtsNames.OrderBy(n => n, StringComparer.Ordinal).ToList();
        VadEngines = new[] { "Silero", "SilenceGate", "None" }
            .Union(services.Registry.VadNames, StringComparer.OrdinalIgnoreCase)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Seed from the running config so "Config" opens showing what Talk is using.
        var voxa = services.Configuration.GetSection("Voxa");
        _selectedStt = voxa["Stt"] ?? "WhisperCpp";
        _selectedTts = voxa["Tts"] ?? "Piper";
        _selectedVad = voxa["Vad:Engine"] ?? "Silero";
        _selectedProfile = voxa["Profile"] ?? "Default";
        _selectedAgent = voxa["Agent:Provider"] ?? "Echo";
        _selectedWhisperModel = voxa["WhisperCpp:Model"] ?? "tiny.en";
        _selectedPiperVoice = voxa["Piper:Voice"] ?? "en_US-amy-low";
        _selectedKokoroVoice = voxa["Kokoro:Voice"] ?? "af_heart";
        _selectedKokoroPrecision = voxa["Kokoro:Precision"] ?? "int8";
        _agentModel = voxa["Agent:Model"] ?? "gpt-4o-mini";

        Regenerate();
    }

    // ── choices (from the live registry + pinned catalogs) ──────────────────

    public IReadOnlyList<string> SttProviders { get; }
    public IReadOnlyList<string> TtsProviders { get; }
    public IReadOnlyList<string> VadEngines { get; }
    public IReadOnlyList<string> Profiles { get; } = ["Default", "LowLatency", "Quality", "Cheap"];
    public IReadOnlyList<string> AgentProviders { get; } = ["Echo", "OpenAI"];
    public IReadOnlyList<string> WhisperModels { get; } = WhisperCppModelCatalog.KnownModels.ToList();
    public IReadOnlyList<string> PiperVoices { get; } = PiperVoiceCatalog.KnownVoices.ToList();
    public IReadOnlyList<string> KokoroVoices { get; } = KokoroCatalog.KnownVoices.ToList();
    public IReadOnlyList<string> KokoroPrecisions { get; } = KokoroCatalog.KnownPrecisions.ToList();

    [ObservableProperty] private string _selectedStt;
    [ObservableProperty] private string _selectedTts;
    [ObservableProperty] private string _selectedVad;
    [ObservableProperty] private string _selectedProfile;
    [ObservableProperty] private string _selectedAgent;
    [ObservableProperty] private string _selectedWhisperModel;
    [ObservableProperty] private string _selectedPiperVoice;
    [ObservableProperty] private string _selectedKokoroVoice;
    [ObservableProperty] private string _selectedKokoroPrecision;

    // ── LLM agent (the "talk to a real model" path) ──────────────────────────

    /// <summary>Chat model for the OpenAI agent provider (e.g. gpt-4o-mini).</summary>
    [ObservableProperty] private string _agentModel;

    /// <summary>
    /// API key for the OpenAI agent. Optional even when the provider is OpenAI: blank falls back
    /// to whatever the environment already provides (<c>Voxa__OpenAI__ApiKey</c>, user-secrets).
    /// Never written into the exported JSON — only applied to the live app.
    /// </summary>
    [ObservableProperty] private string _agentApiKey = "";

    /// <summary>True while a Talk session is live — applying a config swap mid-call is blocked.</summary>
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _applyBlocked;

    [ObservableProperty] private string? _applyStatus;

    public bool ShowWhisperOptions => string.Equals(SelectedStt, "WhisperCpp", StringComparison.OrdinalIgnoreCase);
    public bool ShowPiperOptions => string.Equals(SelectedTts, "Piper", StringComparison.OrdinalIgnoreCase);
    public bool ShowKokoroOptions => string.Equals(SelectedTts, "Kokoro", StringComparison.OrdinalIgnoreCase);
    public bool ShowAgentOptions => string.Equals(SelectedAgent, "OpenAI", StringComparison.OrdinalIgnoreCase);

    [ObservableProperty] private string _exportJson = "";
    [ObservableProperty] private string _validationText = "";
    [ObservableProperty] private bool _isValid;
    public ObservableCollection<string> ValidationErrors { get; } = new();

    partial void OnSelectedSttChanged(string value) { OnPropertyChanged(nameof(ShowWhisperOptions)); Regenerate(); }
    partial void OnSelectedTtsChanged(string value)
    {
        OnPropertyChanged(nameof(ShowPiperOptions));
        OnPropertyChanged(nameof(ShowKokoroOptions));
        Regenerate();
    }
    partial void OnSelectedVadChanged(string value) => Regenerate();
    partial void OnSelectedProfileChanged(string value) => Regenerate();
    partial void OnSelectedAgentChanged(string value) { OnPropertyChanged(nameof(ShowAgentOptions)); Regenerate(); }
    partial void OnSelectedWhisperModelChanged(string value) => Regenerate();
    partial void OnSelectedPiperVoiceChanged(string value) => Regenerate();
    partial void OnSelectedKokoroVoiceChanged(string value) => Regenerate();
    partial void OnSelectedKokoroPrecisionChanged(string value) => Regenerate();
    partial void OnAgentModelChanged(string value) => Regenerate();
    partial void OnAgentApiKeyChanged(string value) => Regenerate();

    // ── draft → JSON + validation ────────────────────────────────────────────

    /// <summary>
    /// The draft as flat configuration pairs — the validator's, the JSON's, and (with secrets)
    /// the Apply path's source. The API key is only ever included when
    /// <paramref name="includeSecrets"/> is true: it goes to the live container, never the export.
    /// </summary>
    internal Dictionary<string, string?> DraftPairs(bool includeSecrets = false)
    {
        var pairs = new Dictionary<string, string?>
        {
            ["Voxa:Stt"] = SelectedStt,
            ["Voxa:Tts"] = SelectedTts,
            ["Voxa:Agent:Provider"] = SelectedAgent,
        };
        if (!string.Equals(SelectedProfile, "Default", StringComparison.OrdinalIgnoreCase))
            pairs["Voxa:Profile"] = SelectedProfile;
        if (!string.Equals(SelectedVad, "Silero", StringComparison.OrdinalIgnoreCase))
            pairs["Voxa:Vad:Engine"] = SelectedVad;
        if (ShowWhisperOptions) pairs["Voxa:WhisperCpp:Model"] = SelectedWhisperModel;
        if (ShowPiperOptions) pairs["Voxa:Piper:Voice"] = SelectedPiperVoice;
        if (ShowKokoroOptions)
        {
            pairs["Voxa:Kokoro:Voice"] = SelectedKokoroVoice;
            pairs["Voxa:Kokoro:Precision"] = SelectedKokoroPrecision;
        }
        if (ShowAgentOptions)
        {
            pairs["Voxa:Agent:Model"] = AgentModel;
            if (includeSecrets && !string.IsNullOrWhiteSpace(AgentApiKey))
                pairs["Voxa:Agent:ApiKey"] = AgentApiKey.Trim();
        }
        return pairs;
    }

    internal void Regenerate()
    {
        ExportJson = ToNestedJson(DraftPairs());
        Validate(DraftPairs(includeSecrets: true));
        ApplyCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Validate the draft through the SAME registration + options-validation path a server runs
    /// at startup, plus the agent factory's credential check: "Studio says valid" ⇒ "the
    /// exported block boots" AND "a Talk session gets an agent". The draft is layered over the
    /// app's live configuration so keys already present in the environment/user-secrets count.
    /// </summary>
    private void Validate(Dictionary<string, string?> pairs)
    {
        ValidationErrors.Clear();
        try
        {
            var draftConfig = new ConfigurationBuilder()
                .AddConfiguration(_services.Configuration)
                .AddInMemoryCollection(pairs)
                .Build();
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(draftConfig); // DefaultAgentFactory resolves it
            services.AddVoxa(draftConfig);
            using var provider = services.BuildServiceProvider();
            var options = provider.GetRequiredService<IOptions<VoxaOptions>>().Value; // triggers the validator

            // The options validator covers providers/profile; agent usability (provider support,
            // credentials) is the factory's check — the same one VoxaDefaultsGuard arms.
            var factory = provider.GetRequiredService<IVoiceAgentFactory>();
            foreach (var problem in factory.Validate(options.Agent))
                ValidationErrors.Add(problem);

            IsValid = ValidationErrors.Count == 0;
            ValidationText = IsValid
                ? "Valid — a server boots with this block."
                : $"{ValidationErrors.Count} problem(s):";
        }
        catch (OptionsValidationException ex)
        {
            IsValid = false;
            foreach (var failure in ex.Failures) ValidationErrors.Add(failure);
            ValidationText = $"{ValidationErrors.Count} problem(s):";
        }
        catch (Exception ex)
        {
            IsValid = false;
            ValidationErrors.Add(ex.Message);
            ValidationText = "Validation failed:";
        }
    }

    // ── apply to the live app ────────────────────────────────────────────────

    private bool CanApply() => IsValid && !ApplyBlocked;

    /// <summary>
    /// Rebuild the live container from this draft (including the API key, which never touches
    /// disk). The NEXT Talk session — and Voice Lab synthesis — composes with these settings.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        try
        {
            _services.Reconfigure(DraftPairs(includeSecrets: true));
            var agent = ShowAgentOptions ? $"{SelectedAgent} ({AgentModel})" : SelectedAgent;
            ApplyStatus = $"Applied — next Talk session: {SelectedStt} → {agent} → {SelectedTts}.";
        }
        catch (Exception ex)
        {
            ApplyStatus = $"Apply failed: {ex.Message}";
        }
    }

    partial void OnIsValidChanged(bool value) => ApplyCommand.NotifyCanExecuteChanged();

    /// <summary>Flat "A:B:C" pairs → pretty-printed nested JSON with a "Voxa" root.</summary>
    internal static string ToNestedJson(IReadOnlyDictionary<string, string?> pairs)
    {
        var root = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
        {
            var parts = key.Split(':');
            var node = root;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (node.TryGetValue(parts[i], out var existing) && existing is SortedDictionary<string, object> child)
                {
                    node = child;
                }
                else
                {
                    var fresh = new SortedDictionary<string, object>(StringComparer.Ordinal);
                    node[parts[i]] = fresh;
                    node = fresh;
                }
            }
            node[parts[^1]] = value ?? "";
        }

        return JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            var dir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var path = Path.Combine(dir, "voxa-appsettings.json");
            await File.WriteAllTextAsync(path, ExportJson);
            ValidationText = $"Saved {path}";
        }
        catch (Exception ex)
        {
            ValidationText = ex.Message;
        }
    }
}
