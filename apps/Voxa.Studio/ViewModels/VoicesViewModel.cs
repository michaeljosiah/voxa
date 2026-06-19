using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Voxa.Speech.Voices;
using Voxa.Studio.Services;

namespace Voxa.Studio.ViewModels;

/// <summary>
/// VVL-001 WS5: the Voices section — a managed voice library over the live providers. Lists every
/// usable voice (local catalog + live cloud voices + your clones) reconciled into Live/Stale/
/// Discovered, and a consent-gated clone wizard. Avalonia-free and headless-testable; provider IO
/// is plain async (no hub stream, so no drain timer). Local cloning is deferred (WS3) — the wizard
/// shows it as coming soon.
/// </summary>
public sealed partial class VoicesViewModel : ObservableObject
{
    private readonly StudioServices _services;
    private readonly VoiceStore _store;

    public VoicesViewModel(StudioServices services) : this(services, new VoiceStore()) { }

    internal VoicesViewModel(StudioServices services, VoiceStore store)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        Catalog = new VoiceCatalogService(services, store);
        CloneSamples.CollectionChanged += (_, _) => CloneCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The reconciliation service — exposed for the cache invalidate and the test seams.</summary>
    internal VoiceCatalogService Catalog { get; }

    /// <summary>Raised by the Audition action — the shell deep-links to the TTS playground.</summary>
    public event Action<LibraryVoice>? AuditionRequested;

    // ── library + provider status ──────────────────────────────────────────
    public ObservableCollection<LibraryVoice> Voices { get; } = [];
    public ObservableCollection<ProviderVoiceSet> Providers { get; } = [];

    private const string AllProviders = "All providers";

    /// <summary>Provider filter options ("All providers" + each provider present in the library).</summary>
    public ObservableCollection<string> ProviderFilters { get; } = [AllProviders];
    [ObservableProperty] private string _selectedProviderFilter = AllProviders;

    /// <summary>Voices after the provider filter — the grid binds to this.</summary>
    public ObservableCollection<LibraryVoice> VisibleVoices { get; } = [];

    [ObservableProperty] private bool _isBusy;

    /// <summary>Set by the shell: a live Talk/Builder/Metrics run blocks recording a sample.</summary>
    [ObservableProperty] private bool _recordBlocked;

    // ── clone wizard ───────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloneCommand))]
    private string _cloneName = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloneCommand))]
    private bool _consentAttested;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloneCommand))]
    private string? _selectedCloneTarget;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloneCommand))]
    private bool _isCloning;

    [ObservableProperty] private string? _cloneError;

    /// <summary>Reference clips for the pending clone. The view adds these via a file picker / recorder.</summary>
    public ObservableCollection<VoiceSample> CloneSamples { get; } = [];

    /// <summary>Providers that can clone right now (cloner capability + a resolvable key).</summary>
    public ObservableCollection<string> CloneTargets { get; } = [];

    /// <summary>Local ONNX cloning is a deferred follow-up (VVL-001 WS3) — the UI shows "coming soon".</summary>
    public bool LocalCloningAvailable => false;

    /// <summary>Re-read the library from disk and the providers live. Called on nav-in and after Config Apply.</summary>
    public void Refresh()
    {
        Catalog.Invalidate();
        RefreshCommand.Execute(null);
    }

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            // No ConfigureAwait(false): the continuation mutates UI-bound collections below, so it
            // must resume on the UI thread (the Studio VM convention; Avalonia rejects off-thread).
            var sets = await Catalog.AllAsync(ct);

            Providers.Clear();
            Voices.Clear();
            CloneTargets.Clear();
            foreach (var set in sets)
            {
                Providers.Add(set);
                foreach (var row in set.Voices) Voices.Add(row);
                if (!set.MissingKey && Catalog.CanClone(set.Provider))
                    CloneTargets.Add(set.Provider);
            }

            // Keep the wizard's target valid as keys/providers change.
            if (SelectedCloneTarget is null || !CloneTargets.Contains(SelectedCloneTarget))
                SelectedCloneTarget = CloneTargets.FirstOrDefault();

            RebuildProviderFilters();
            ApplyVoiceFilter();
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSelectedProviderFilterChanged(string value) => ApplyVoiceFilter();

    /// <summary>The providers present in the library, so the filter only offers real choices.</summary>
    private void RebuildProviderFilters()
    {
        var providers = Voices.Select(v => v.Voice.ProviderName)
            .Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();
        ProviderFilters.Clear();
        ProviderFilters.Add(AllProviders);
        foreach (var p in providers) ProviderFilters.Add(p);
        if (!ProviderFilters.Contains(SelectedProviderFilter)) SelectedProviderFilter = AllProviders;
    }

    private void ApplyVoiceFilter()
    {
        VisibleVoices.Clear();
        foreach (var v in Voices.Where(v =>
            SelectedProviderFilter == AllProviders || v.Voice.ProviderName == SelectedProviderFilter))
            VisibleVoices.Add(v);
    }

    /// <summary>Add a reference clip to the pending clone (the view supplies bytes from a file/recording).</summary>
    public void AddSample(VoiceSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        CloneSamples.Add(sample);
    }

    [RelayCommand]
    private void RemoveSample(VoiceSample sample) => CloneSamples.Remove(sample);

    // The consent gate: nothing clones without a name, a sample, a target, and an explicit tick.
    private bool CanClone()
        => !IsCloning
           && !string.IsNullOrWhiteSpace(CloneName)
           && CloneSamples.Count > 0
           && !string.IsNullOrWhiteSpace(SelectedCloneTarget)
           && ConsentAttested;

    [RelayCommand(CanExecute = nameof(CanClone))]
    private async Task CloneAsync(CancellationToken ct)
    {
        var target = SelectedCloneTarget!;
        var samples = CloneSamples.ToList();
        var request = new VoiceCloneRequest(CloneName.Trim(), samples);

        IsCloning = true;
        CloneError = null;
        try
        {
            // Stay on the UI thread — the wizard reset + RefreshAsync below mutate bound state.
            var voice = await Catalog.CloneAsync(target, request, ct);

            // Record the clone with its consent attestation and the reference clips.
            _store.Save(new VoiceProfile
            {
                DisplayName = voice.DisplayName,
                ProviderName = target,
                ProviderVoiceId = voice.Id,
                Kind = VoiceKind.Cloned,
                Language = voice.Language,
                ConsentAttestedAt = DateTimeOffset.Now,
            }, samples);

            // Reset the wizard and surface the new voice.
            CloneName = "";
            ConsentAttested = false;
            CloneSamples.Clear();
            await RefreshAsync(ct);
        }
        catch (VoiceProviderException ex)
        {
            CloneError = ex.Message;   // plan-gated / missing key / rejected — shown, never thrown to the UI
        }
        finally
        {
            IsCloning = false;
        }
    }

    /// <summary>Annotate a discovered provider voice into the local library (no consent — not a new clone).</summary>
    [RelayCommand]
    private void AddToLibrary(LibraryVoice row)
    {
        if (row is null || row.Profile is not null) return;
        _store.Save(new VoiceProfile
        {
            DisplayName = row.Voice.DisplayName,
            ProviderName = row.Voice.ProviderName,
            ProviderVoiceId = row.Voice.Id,
            Kind = row.Voice.Kind,
            Language = row.Voice.Language,
        });
        Refresh();
    }

    /// <summary>Remove a profile from the local library (does not delete the voice on the provider).</summary>
    [RelayCommand]
    private void Forget(LibraryVoice row)
    {
        if (row?.Profile is null) return;
        _store.Delete(row.Profile);
        Refresh();
    }

    [RelayCommand]
    private void Audition(LibraryVoice row)
    {
        if (row is not null) AuditionRequested?.Invoke(row);
    }
}
