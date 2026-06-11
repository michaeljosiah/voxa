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

    public bool ShowWhisperOptions => string.Equals(SelectedStt, "WhisperCpp", StringComparison.OrdinalIgnoreCase);
    public bool ShowPiperOptions => string.Equals(SelectedTts, "Piper", StringComparison.OrdinalIgnoreCase);
    public bool ShowKokoroOptions => string.Equals(SelectedTts, "Kokoro", StringComparison.OrdinalIgnoreCase);

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
    partial void OnSelectedAgentChanged(string value) => Regenerate();
    partial void OnSelectedWhisperModelChanged(string value) => Regenerate();
    partial void OnSelectedPiperVoiceChanged(string value) => Regenerate();
    partial void OnSelectedKokoroVoiceChanged(string value) => Regenerate();
    partial void OnSelectedKokoroPrecisionChanged(string value) => Regenerate();

    // ── draft → JSON + validation ────────────────────────────────────────────

    /// <summary>The draft as flat configuration pairs — both the validator's and the JSON's source.</summary>
    internal Dictionary<string, string?> DraftPairs()
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
        return pairs;
    }

    internal void Regenerate()
    {
        var pairs = DraftPairs();
        ExportJson = ToNestedJson(pairs);
        Validate(pairs);
    }

    /// <summary>
    /// Validate the draft through the SAME registration + options-validation path a server runs
    /// at startup: "Studio says valid" ⇒ "the exported block boots".
    /// </summary>
    private void Validate(Dictionary<string, string?> pairs)
    {
        ValidationErrors.Clear();
        try
        {
            var draftConfig = new ConfigurationBuilder().AddInMemoryCollection(pairs).Build();
            var services = new ServiceCollection();
            services.AddVoxa(draftConfig);
            using var provider = services.BuildServiceProvider();
            _ = provider.GetRequiredService<IOptions<VoxaOptions>>().Value; // triggers the validator

            IsValid = true;
            ValidationText = "Valid — a server boots with this block.";
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
