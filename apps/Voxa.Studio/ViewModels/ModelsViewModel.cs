using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Voxa.Speech;
using Voxa.Studio.Services;

namespace Voxa.Studio.ViewModels;

/// <summary>One cache entry row, joined against the catalogs when recognizable.</summary>
public sealed partial class ModelRow : ObservableObject
{
    public required CachedModelInfo Entry { get; init; }
    public required string EngineLabel { get; init; }   // "Whisper", "Piper", "Kokoro", or "—" for foreign files
    public required bool IsKnown { get; init; }
    public required VoxaModelArtifact? Artifact { get; init; }

    [ObservableProperty] private string _verifyState = "·";   // "·" unverified, "✓", "✗", "…"
    [ObservableProperty] private bool _isBusy;

    public string Id => Entry.Id;
    public string SizeText => Entry.SizeBytes >= 1024 * 1024
        ? $"{Entry.SizeBytes / (1024.0 * 1024):F0} MB"
        : $"{Entry.SizeBytes / 1024.0:F0} KB";
    public string KindText => Entry.IsExtractedArchive ? "archive" : "file";

    /// <summary>Provider/engine this row belongs to ("Whisper" / "Piper" / "Kokoro" / "—").</summary>
    public string Provider => EngineLabel;

    /// <summary>Pipeline category for the tab grouping.</summary>
    public string Category => EngineLabel switch
    {
        "Whisper" => "STT",
        "Piper" or "Kokoro" => "TTS",
        _ => "Other",
    };
}

/// <summary>
/// The Models view (VST-001 WS4): a visual front-end for <see cref="VoxaModelCache"/> —
/// inventory, streamed SHA-256 re-verification, prefetch-the-full-catalog (air-gap
/// provisioning), and purge. Shares the exact directory the engines resolve from, including
/// its cross-process download locks.
/// </summary>
public sealed partial class ModelsViewModel : ObservableObject
{
    private readonly StudioServices _services;

    public ModelsViewModel(StudioServices services)
    {
        _services = services;
        CacheRoot = services.ModelCache.Options.CacheRoot;
        CacheRootSource = Environment.GetEnvironmentVariable(VoxaModelCacheOptions.CacheRootEnvVar) is not null
            ? $"from {VoxaModelCacheOptions.CacheRootEnvVar}"
            : services.Configuration["Voxa:Models:CachePath"] is not null
                ? "from Voxa:Models:CachePath"
                : "OS default";
        Refresh();
    }

    public ObservableCollection<ModelRow> Rows { get; } = new();

    private const string AllProviders = "All providers";

    /// <summary>Logical tabs: All, then the pipeline stages, then foreign files.</summary>
    public IReadOnlyList<string> Tabs { get; } = ["All", "STT", "TTS", "Other"];
    [ObservableProperty] private string _selectedTab = "All";

    /// <summary>Provider options for the current tab (always begins with "All providers").</summary>
    public ObservableCollection<string> ProviderFilters { get; } = new();
    [ObservableProperty] private string _selectedProvider = AllProviders;

    /// <summary>Rows after the tab + provider filter — the list the view binds to.</summary>
    public ObservableCollection<ModelRow> VisibleRows { get; } = new();

    public string CacheRoot { get; }
    public string CacheRootSource { get; }

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private bool _isPrefetching;
    [ObservableProperty] private double _prefetchProgress; // 0..1
    [ObservableProperty] private string _totalSizeText = "";

    [RelayCommand]
    public void Refresh()
    {
        ErrorText = null;
        Rows.Clear();

        // Join inventory against every pinned artifact this machine can use.
        var known = ActiveConfigArtifacts.FullCatalog()
            .GroupBy(ArtifactCacheKey)
            .ToDictionary(g => g.Key, g => g.First());

        long total = 0;
        foreach (var entry in _services.ModelCache.Enumerate().OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            total += entry.SizeBytes;
            known.TryGetValue(entry.Id, out var artifact);
            Rows.Add(new ModelRow
            {
                Entry = entry,
                Artifact = artifact,
                IsKnown = artifact is not null,
                EngineLabel = EngineFor(entry.Id),
            });
        }
        TotalSizeText = $"{total / (1024.0 * 1024):F0} MB on disk";
        StatusText = Rows.Count == 0
            ? "Cache is empty — models download on first use, or prefetch the full catalog below."
            : $"{Rows.Count} entries.";

        RebuildProviderFilters();
        ApplyFilter();
    }

    partial void OnSelectedTabChanged(string value)
    {
        RebuildProviderFilters();
        ApplyFilter();
    }

    partial void OnSelectedProviderChanged(string value) => ApplyFilter();

    private bool InTab(ModelRow r) => SelectedTab == "All" || r.Category == SelectedTab;

    /// <summary>The providers present in the current tab, so the filter only offers real choices.</summary>
    private void RebuildProviderFilters()
    {
        var providers = Rows.Where(InTab).Select(r => r.Provider)
            .Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();
        ProviderFilters.Clear();
        ProviderFilters.Add(AllProviders);
        foreach (var p in providers) ProviderFilters.Add(p);
        if (!ProviderFilters.Contains(SelectedProvider)) SelectedProvider = AllProviders;
    }

    private void ApplyFilter()
    {
        VisibleRows.Clear();
        foreach (var r in Rows.Where(r =>
            InTab(r) && (SelectedProvider == AllProviders || r.Provider == SelectedProvider)))
            VisibleRows.Add(r);
    }

    /// <summary>The inventory id an artifact appears under (extracted archives get the .extracted suffix).</summary>
    private static string ArtifactCacheKey(VoxaModelArtifact a)
        => a.ArchiveEntry is null ? a.Id : a.Id + ".extracted";

    private static string EngineFor(string id) =>
        id.StartsWith("whisper/", StringComparison.Ordinal) ? "Whisper" :
        id.StartsWith("piper/", StringComparison.Ordinal) ? "Piper" :
        id.StartsWith("kokoro/", StringComparison.Ordinal) ? "Kokoro" : "—";

    // ── actions ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task VerifyAsync(ModelRow row)
    {
        if (row.Artifact is null) { row.VerifyState = "?"; return; }
        row.IsBusy = true;
        row.VerifyState = "…";
        try
        {
            var ok = await _services.ModelCache.VerifyAsync(row.Artifact, CancellationToken.None);
            row.VerifyState = ok ? "✓" : "✗";
            if (!ok)
                StatusText = $"{row.Id} FAILED verification — purge it and re-download.";
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            row.VerifyState = "✗";
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task VerifyAllAsync()
    {
        foreach (var row in Rows.Where(r => r.IsKnown).ToList())
            await VerifyAsync(row);
        StatusText = "Verification pass complete.";
    }

    [RelayCommand]
    private void Purge(ModelRow row)
    {
        try
        {
            _services.ModelCache.Purge(row.Entry);
            StatusText = $"Purged {row.Id}.";
            Refresh();
        }
        catch (IOException ex)
        {
            // Locked by a concurrent download (possibly another Voxa process) — surface verbatim.
            ErrorText = ex.Message;
        }
    }

    [RelayCommand]
    private async Task PrefetchAllAsync()
    {
        if (IsPrefetching) return;
        IsPrefetching = true;
        ErrorText = null;
        try
        {
            var all = ActiveConfigArtifacts.FullCatalog();
            var missing = all.Where(a => !_services.ModelCache.IsCached(a)).ToList();
            if (missing.Count == 0)
            {
                StatusText = "Everything in the catalog is already cached — this machine is air-gap ready.";
                return;
            }

            var totalMb = missing.Sum(a => a.SizeBytes) / (1024 * 1024);
            StatusText = $"Prefetching {missing.Count} artifacts, ~{totalMb} MB…";

            var failures = await PrefetchEachAsync(
                missing, a => _services.ModelCache.PrefetchAsync([a]));

            StatusText = failures.Count == 0
                ? $"Prefetch complete — copy {CacheRoot} to provision an air-gapped machine."
                : $"Prefetched {missing.Count - failures.Count}/{missing.Count} — " +
                  $"failed: {string.Join(", ", failures.Select(f => f.Id))}.";
            if (failures.Count > 0)
                ErrorText = string.Join("\n\n", failures.Select(f => f.Error));
            Refresh();
        }
        finally
        {
            IsPrefetching = false;
            PrefetchProgress = 0;
        }
    }

    /// <summary>
    /// Bulk provisioning fetches each artifact independently: one stale pin or flaky download
    /// must not abort the other 20 — a Talk session needs ALL of its artifacts, but air-gap
    /// prefetch wants as many as it can get, with the casualties listed at the end.
    /// </summary>
    internal async Task<List<(string Id, string Error)>> PrefetchEachAsync(
        IReadOnlyList<VoxaModelArtifact> missing, Func<VoxaModelArtifact, Task> fetch)
    {
        var failures = new List<(string, string)>();
        for (int i = 0; i < missing.Count; i++)
        {
            StatusText = $"({i + 1}/{missing.Count}) {missing[i].Id}";
            try
            {
                await fetch(missing[i]);
            }
            catch (Exception ex)
            {
                failures.Add((missing[i].Id, ex.Message));
            }
            PrefetchProgress = (double)(i + 1) / missing.Count;
        }
        return failures;
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(CacheRoot);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = CacheRoot,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }
}
