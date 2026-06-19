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
        VoiceCatalog = new VoiceCatalogService(services, new VoiceStore());

        VadEngines = new[] { "Silero", "SilenceGate", "None" }
            .Union(services.Registry.VadNames, StringComparer.OrdinalIgnoreCase)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        RefreshProviderLists();   // VST-003: the dropdowns are the registry filtered to activated-or-local

        // Seed from the running config so "Config" opens showing what Talk is using.
        var voxa = services.Configuration.GetSection("Voxa");
        _selectedStt = voxa["Stt"] ?? "WhisperCpp";
        _selectedTts = voxa["Tts"] ?? "Piper";
        _selectedVad = voxa["Vad:Engine"] ?? "Silero";
        _selectedProfile = voxa["Profile"] ?? "Default";
        _selectedAgent = voxa["Agent:Provider"] ?? "Echo";
        _selectedWhisperModel = voxa["WhisperCpp:Model"] ?? "tiny.en";
        _selectedWhisperDevice = voxa["WhisperCpp:Device"] ?? "cpu";
        _selectedPiperVoice = voxa["Piper:Voice"] ?? "en_US-amy-low";
        _selectedKokoroVoice = voxa["Kokoro:Voice"] ?? "af_heart";
        _selectedKokoroPrecision = voxa["Kokoro:Precision"] ?? "int8";
        _agentModel = voxa["Agent:Model"] ?? "gpt-4o-mini";
        _agentBaseUrl = voxa["Agent:BaseUrl"] ?? "http://localhost:11434/v1";

        Regenerate();
        // NB: do NOT load cloud voices here. ConfigViewModel is constructed eagerly at shell
        // startup, and a cloud TTS + key would make a live HTTP call before the user acts (the
        // "Studio never touches the network before the user acts" invariant). The shell calls
        // RefreshCloudVoices() when the Config section is first opened (a user action).
    }

    // ── choices (from the live registry + pinned catalogs) ──────────────────

    // VST-003: filtered to activated-or-local identities; rebuilt by RefreshProviderLists().
    public ObservableCollection<string> SttProviders { get; } = new();
    public ObservableCollection<string> TtsProviders { get; } = new();
    public ObservableCollection<string> AgentProviders { get; } = new();
    public IReadOnlyList<string> VadEngines { get; }
    public IReadOnlyList<string> Profiles { get; } = ["Default", "LowLatency", "Quality", "Cheap"];
    public IReadOnlyList<string> WhisperModels { get; } = WhisperCppModelCatalog.KnownModels.ToList();
    public IReadOnlyList<string> WhisperDevices { get; } = Enum.GetNames<WhisperDevice>().Select(n => n.ToLowerInvariant()).ToList();
    public IReadOnlyList<string> PiperVoices { get; } = PiperVoiceCatalog.KnownVoices.ToList();
    public IReadOnlyList<string> KokoroVoices { get; } = KokoroCatalog.KnownVoices.ToList();
    public IReadOnlyList<string> KokoroPrecisions { get; } = KokoroCatalog.KnownPrecisions.ToList();

    [ObservableProperty] private string _selectedStt;
    [ObservableProperty] private string _selectedTts;
    [ObservableProperty] private string _selectedVad;
    [ObservableProperty] private string _selectedProfile;
    [ObservableProperty] private string _selectedAgent;
    [ObservableProperty] private string _selectedWhisperModel;
    [ObservableProperty] private string _selectedWhisperDevice;
    [ObservableProperty] private string _selectedPiperVoice;
    [ObservableProperty] private string _selectedKokoroVoice;
    [ObservableProperty] private string _selectedKokoroPrecision;

    // Agent providers aren't registry STT/TTS entries — they're DefaultAgentFactory's pair.
    private static readonly string[] AgentProviderNames = ["Echo", "OpenAI", "Ollama"];

    /// <summary>
    /// Rebuild the STT/TTS/Agent dropdowns from the live registry, filtered to identities that are
    /// local (always available) or activated in Settings (VST-003 WS6). Called at construction and
    /// after the Settings dialog changes activations. The current selection is left as-is even if it
    /// falls outside the filtered list — an appsettings-configured provider stays selected and exports
    /// correctly; the dropdown simply won't offer un-activated alternatives.
    /// </summary>
    public void RefreshProviderLists()
    {
        var activated = _services.Secrets.Activated
            .Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        ReplaceList(SttProviders, _services.Registry.SttNames, ProviderRole.Stt, activated);
        ReplaceList(TtsProviders, _services.Registry.TtsNames, ProviderRole.Tts, activated);
        ReplaceList(AgentProviders, AgentProviderNames, ProviderRole.Agent, activated);
    }

    private static void ReplaceList(
        ObservableCollection<string> target, IEnumerable<string> source,
        ProviderRole role, IReadOnlySet<string> activated)
    {
        var kept = source
            .Where(name => Visible(name, role, activated))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        target.Clear();
        foreach (var name in kept) target.Add(name);
    }

    private static bool Visible(string name, ProviderRole role, IReadOnlySet<string> activated)
    {
        var manifest = ProviderManifestCatalog.Find(name);
        if (manifest is null) return true;                  // unmanifested → don't hide
        if (!manifest.Roles.Contains(role)) return false;   // described, but not for this role
        return manifest.IsLocal || activated.Contains(name);
    }

    // ── LLM agent (the "talk to a real model" path) ──────────────────────────

    /// <summary>Chat model for the OpenAI agent provider (e.g. gpt-4o-mini).</summary>
    [ObservableProperty] private string _agentModel;

    /// <summary>
    /// API key for the OpenAI agent. Optional even when the provider is OpenAI: blank falls back
    /// to whatever the environment already provides (<c>Voxa__OpenAI__ApiKey</c>, user-secrets).
    /// Never written into the exported JSON — only applied to the live app.
    /// </summary>
    [ObservableProperty] private string _agentApiKey = "";

    /// <summary>Base URL for the Ollama agent's OpenAI-compatible endpoint. Keyless and local.</summary>
    [ObservableProperty] private string _agentBaseUrl = "http://localhost:11434/v1";

    /// <summary>True while a Talk session is live — applying a config swap mid-call is blocked.</summary>
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _applyBlocked;

    [ObservableProperty] private string? _applyStatus;

    // ── cloud/cloned voice picker (VVL-001 WS6) ──────────────────────────────
    // ElevenLabs/Mistral/VoiceClone get a dynamic voice list from the live library (cloned voices
    // included) instead of a hand-typed id. Piper/Kokoro keep their compiled-in catalog pickers.

    /// <summary>The reconciliation service behind the cloud voice list — exposed for the test seam.</summary>
    internal VoiceCatalogService VoiceCatalog { get; }

    /// <summary>Voice ids offered by the selected cloud/clone provider (live + your clones).</summary>
    public ObservableCollection<string> CloudVoices { get; } = new();

    [ObservableProperty] private string? _selectedCloudVoice;

    /// <summary>True when the selected cloud provider has no resolvable key — the list can't load.</summary>
    [ObservableProperty] private bool _cloudVoiceKeyRequired;

    public bool ShowWhisperOptions => string.Equals(SelectedStt, "WhisperCpp", StringComparison.OrdinalIgnoreCase);
    public bool ShowPiperOptions => string.Equals(SelectedTts, "Piper", StringComparison.OrdinalIgnoreCase);
    public bool ShowKokoroOptions => string.Equals(SelectedTts, "Kokoro", StringComparison.OrdinalIgnoreCase);
    public bool ShowCloudVoiceOptions => CloudVoiceKeyFor(SelectedTts) is not null;
    public bool ShowAgentOptions => ShowAgentApiKey || ShowAgentBaseUrl;
    public bool ShowAgentApiKey => string.Equals(SelectedAgent, "OpenAI", StringComparison.OrdinalIgnoreCase);
    public bool ShowAgentBaseUrl => string.Equals(SelectedAgent, "Ollama", StringComparison.OrdinalIgnoreCase);

    // The config key each library-backed provider selects its voice with.
    private static string? CloudVoiceKeyFor(string? tts) => tts?.ToLowerInvariant() switch
    {
        "elevenlabs" => "Voxa:ElevenLabs:VoiceId",
        "mistral"    => "Voxa:Mistral:Voice",
        "voiceclone" => "Voxa:VoiceClone:Voice",
        _ => null,
    };

    /// <summary>
    /// Load the cloud voice list for the selected provider (live + cloned, key permitting). Triggered
    /// by a user action — opening the Config section or changing the TTS provider — never at startup.
    /// </summary>
    public void RefreshCloudVoices() => _ = ReloadCloudVoicesAsync();

    internal async Task ReloadCloudVoicesAsync()
    {
        // Capture the provider this request is for, up front — the user may switch providers during
        // the await (these calls are fire-and-forget from OnSelectedTtsChanged), and resuming against
        // the *current* SelectedTts would null-deref CloudVoiceKeyFor for Piper/Kokoro or write the
        // wrong provider's voices into the picker.
        var provider = SelectedTts;
        var voiceKey = CloudVoiceKeyFor(provider);

        CloudVoices.Clear();
        CloudVoiceKeyRequired = false;
        if (voiceKey is null) { SelectedCloudVoice = null; return; }

        // No ConfigureAwait(false): the continuation mutates UI-bound CloudVoices, so it must
        // resume on the UI thread (Avalonia rejects off-thread collection changes).
        var set = await VoiceCatalog.ForProviderAsync(provider, CancellationToken.None);

        // Discard a stale result if the selection changed while the request was in flight.
        if (!string.Equals(provider, SelectedTts, StringComparison.OrdinalIgnoreCase)) return;

        CloudVoiceKeyRequired = set.MissingKey;
        foreach (var v in set.Voices) CloudVoices.Add(v.Voice.Id);

        // Seed from the running config so the picker opens on the configured voice.
        var current = _services.Configuration[voiceKey];
        SelectedCloudVoice = !string.IsNullOrWhiteSpace(current) && CloudVoices.Contains(current)
            ? current
            : CloudVoices.FirstOrDefault();
    }

    partial void OnSelectedCloudVoiceChanged(string? value) => Regenerate();

    [ObservableProperty] private string _exportJson = "";
    [ObservableProperty] private string _validationText = "";
    [ObservableProperty] private bool _isValid;
    public ObservableCollection<string> ValidationErrors { get; } = new();

    partial void OnSelectedSttChanged(string value) { OnPropertyChanged(nameof(ShowWhisperOptions)); Regenerate(); }
    partial void OnSelectedTtsChanged(string value)
    {
        OnPropertyChanged(nameof(ShowPiperOptions));
        OnPropertyChanged(nameof(ShowKokoroOptions));
        OnPropertyChanged(nameof(ShowCloudVoiceOptions));
        Regenerate();
        _ = ReloadCloudVoicesAsync();
    }
    partial void OnSelectedVadChanged(string value) => Regenerate();
    partial void OnSelectedProfileChanged(string value) => Regenerate();
    partial void OnSelectedAgentChanged(string value)
    {
        // Swap the model placeholder so the field reads sensibly for the chosen provider.
        if (string.Equals(value, "Ollama", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(AgentModel, "gpt-4o-mini", StringComparison.OrdinalIgnoreCase))
            AgentModel = "llama3.2";
        else if (string.Equals(value, "OpenAI", StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(AgentModel, "llama3.2", StringComparison.OrdinalIgnoreCase))
            AgentModel = "gpt-4o-mini";

        OnPropertyChanged(nameof(ShowAgentOptions));
        OnPropertyChanged(nameof(ShowAgentApiKey));
        OnPropertyChanged(nameof(ShowAgentBaseUrl));
        Regenerate();
    }
    partial void OnSelectedWhisperModelChanged(string value) => Regenerate();
    partial void OnSelectedWhisperDeviceChanged(string value) => Regenerate();
    partial void OnSelectedPiperVoiceChanged(string value) => Regenerate();
    partial void OnSelectedKokoroVoiceChanged(string value) => Regenerate();
    partial void OnSelectedKokoroPrecisionChanged(string value) => Regenerate();
    partial void OnAgentModelChanged(string value) => Regenerate();
    partial void OnAgentApiKeyChanged(string value) => Regenerate();
    partial void OnAgentBaseUrlChanged(string value) => Regenerate();

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
        if (ShowWhisperOptions && !string.Equals(SelectedWhisperDevice, "cpu", StringComparison.OrdinalIgnoreCase))
            pairs["Voxa:WhisperCpp:Device"] = SelectedWhisperDevice;
        if (ShowPiperOptions) pairs["Voxa:Piper:Voice"] = SelectedPiperVoice;
        if (ShowKokoroOptions)
        {
            pairs["Voxa:Kokoro:Voice"] = SelectedKokoroVoice;
            pairs["Voxa:Kokoro:Precision"] = SelectedKokoroPrecision;
        }
        // A library-backed cloud voice (ElevenLabs/Mistral/VoiceClone) writes its provider's key —
        // the voice id only, never an API key.
        if (CloudVoiceKeyFor(SelectedTts) is { } voiceKey && !string.IsNullOrWhiteSpace(SelectedCloudVoice))
            pairs[voiceKey] = SelectedCloudVoice;
        if (ShowAgentOptions) pairs["Voxa:Agent:Model"] = AgentModel;
        if (ShowAgentApiKey && includeSecrets && !string.IsNullOrWhiteSpace(AgentApiKey))
            pairs["Voxa:Agent:ApiKey"] = AgentApiKey.Trim();
        if (ShowAgentBaseUrl && !string.IsNullOrWhiteSpace(AgentBaseUrl) &&
            !string.Equals(AgentBaseUrl, "http://localhost:11434/v1", StringComparison.OrdinalIgnoreCase))
            pairs["Voxa:Agent:BaseUrl"] = AgentBaseUrl.Trim();
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
            // A raw Config Apply is a one-off override — it no longer matches a saved profile, so clear
            // the active one (the shell's profile bar then reads "Custom").
            _services.Profiles.SetActive(null);
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

    /// <summary>§5 cross-navigation: the shell turns the current draft into a Builder graph.</summary>
    public event Action? OpenInBuilderRequested;

    [RelayCommand]
    private void OpenInBuilder() => OpenInBuilderRequested?.Invoke();

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
