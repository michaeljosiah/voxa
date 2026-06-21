using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Voxa.Studio.Services;

namespace Voxa.Studio.ViewModels;

/// <summary>One stage chip in the shell's pipeline-profile bar (VST-005): a stage-coloured dot + name,
/// with a chevron to the next (<see cref="ShowSeparator"/> is false on the last chip).</summary>
public sealed record ShellChainNode(string Stage, string Name, bool ShowSeparator);

/// <summary>
/// Shell view model: the nav rail's six sections and the shared session coordination
/// (one audio device and one CPU — Talk, Builder, and Metrics runs disable each other
/// while a session is live, and playground playback/capture pauses too).
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    public MainWindowViewModel(StudioServices services)
    {
        Services = services;
        Talk = new TalkViewModel(services);
        Playgrounds = new PlaygroundsViewModel(services);
        Voices = new VoicesViewModel(services);
        Builder = new BuilderViewModel(services);
        Metrics = new MetricsViewModel(services);
        Models = new ModelsViewModel(services);
        Config = new ConfigViewModel(services);
        Diarization = new DiarizationViewModel(services);

        // One audio device — and one set of cores: a concurrent session would also make every
        // Metrics number a lie (the R4 contention rule), so live surfaces block each other.
        Talk.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(TalkViewModel.IsRunning)) return;
            Toasts.Show(
                Talk.IsRunning ? ToastTone.Success : ToastTone.Info,
                Talk.IsRunning ? "Session started" : "Session ended",
                Talk.IsRunning ? "Mic open · pipeline live" : null);
            SyncLiveState();
        };
        Builder.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BuilderViewModel.IsRunning)) SyncLiveState();
        };
        Metrics.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MetricsViewModel.IsRunning)) SyncLiveState();
        };

        // Config's "Open in Builder": the current draft becomes a graph (§5 cross-navigation).
        Config.OpenInBuilderRequested += () =>
        {
            Builder.SeedFromPairs(Config.DraftPairs(includeSecrets: true));
            SelectedSection = 3;
        };

        // Talk's waterfall deep-link: land on that stage's series in the workbench (§5).
        Talk.OpenInMetricsRequested += stage =>
        {
            Metrics.FocusStage(stage);
            SelectedSection = 4;
        };

        // Voices' audition: open the TTS lab and preselect the voice (its tested synth + playback).
        // A local catalog voice (Piper/Kokoro) is selected for real; a cloud/cloned voice the lab
        // can't synthesize just opens the lab (TrySelectVoice returns false, selection untouched).
        Voices.AuditionRequested += v =>
        {
            Playgrounds.SelectedLab = 1;   // TTS lab
            Playgrounds.Tts.TrySelectVoice(v.Voice.ProviderName, v.Voice.Id);
            SelectedSection = 1;
        };

        // Config "Apply" rebuilt the container — every view re-reads from it.
        services.Reconfigured += () =>
        {
            Talk.RefreshFromConfig();
            Playgrounds.RefreshCacheState();
            Voices.Refresh();
            Config.RefreshCloudVoices();   // keys may have changed; Apply is a user action
            Models.Refresh();
            RefreshActiveChain();   // a Config Apply may have changed the stage names shown in the bar chips
            Toasts.Show(ToastTone.Info, "Configuration applied", "Pipeline rebuilt from the active config.");
            // Pre-warm the newly-applied model (cached-only, background) so the next Talk start is warm.
            _ = Task.Run(() => services.WarmUpAsync(cachedOnly: true));
        };

        // The pipeline-profile bar mirrors the store — refresh when one is saved/deleted/activated.
        services.Profiles.Changed += RefreshProfiles;
        RefreshProfiles();
        RefreshActiveChain();
    }

    /// <summary>True when a Settings save arrived during a live run and its apply is waiting for the run to stop.</summary>
    private bool _settingsApplyPending;

    /// <summary>
    /// The Settings dialog saved (VST-003): push the stored credentials into the live container as
    /// the secrets layer — so a later Config "Apply" won't wipe them — and refresh the dropdowns that
    /// depend on which providers are activated.
    /// <para>
    /// Applying rebuilds the container — <see cref="StudioServices.ApplySecrets"/> disposes the old
    /// <c>ServiceProvider</c> — so it must NOT run under a live Talk/Builder/Metrics session whose
    /// scope belongs to that provider (the same reason <c>Config.ApplyBlocked</c> guards Config Apply).
    /// If a run is live, defer the apply until it stops; the secrets are already on disk (the dialog's
    /// Save persisted them), so nothing is lost in the meantime.
    /// </para>
    /// </summary>
    public void OnSettingsSaved()
    {
        if (IsLive)
        {
            _settingsApplyPending = true;
            Toasts.Show(ToastTone.Warning, "Settings saved", "Applies when the live run stops.");
            return;
        }
        ApplySavedSettings();
    }

    private void ApplySavedSettings()
    {
        Services.ApplySecrets(Services.Secrets.BuildConfigPairs()); // fires Reconfigured → views refresh
        Config.RefreshProviderLists();
        // Re-validate the Config draft against the new keys: a provider that was invalid only because
        // its key was missing now re-enables Apply without the user having to touch an unrelated knob.
        Config.Regenerate();
    }

    private void SyncLiveState()
    {
        var live = Talk.IsRunning || Builder.IsRunning || Metrics.IsRunning;
        Playgrounds.Tts.PlaybackBlocked = live;
        Playgrounds.Stt.CaptureBlocked = live;
        Voices.RecordBlocked = live;   // one audio device — recording a sample waits for the run
        // Talk: a live session's scope belongs to the old container. Metrics: the bundle is
        // evidence of the config the run started with — swapping the live config (and firing
        // the Reconfigured refresh) under a recording invites confusion even though the run's
        // ephemeral container and start-captured snapshot survive it.
        Config.ApplyBlocked = Talk.IsRunning || Metrics.IsRunning;
        Builder.RunBlocked = Talk.IsRunning || Metrics.IsRunning;
        Talk.StartBlocked = Builder.IsRunning || Metrics.IsRunning;
        Metrics.RunBlocked = Talk.IsRunning || Builder.IsRunning;
        OnPropertyChanged(nameof(IsLive));
        NotifyBlockedState();

        // A Settings save that arrived mid-run applies now that nothing is live.
        if (!live && _settingsApplyPending)
        {
            _settingsApplyPending = false;
            ApplySavedSettings();
        }
    }

    /// <summary>The shell toast queue (VST-005 WS6) — session edges, profile switches, applies.</summary>
    public ToastService Toasts { get; } = new();

    public StudioServices Services { get; }
    public TalkViewModel Talk { get; }
    public PlaygroundsViewModel Playgrounds { get; }
    public VoicesViewModel Voices { get; }
    public BuilderViewModel Builder { get; }
    public MetricsViewModel Metrics { get; }
    public ModelsViewModel Models { get; }
    public ConfigViewModel Config { get; }
    public DiarizationViewModel Diarization { get; }

    /// <summary>0 Talk, 1 Playgrounds, 2 Voices, 3 Builder, 4 Metrics, 5 Models, 6 Config, 7 Diarization — the nav rail.</summary>
    [ObservableProperty] private int _selectedSection;

    public bool IsLive => Talk.IsRunning || Builder.IsRunning || Metrics.IsRunning;

    // ── contended-surface blocking (VST-005): one audio device + CPU, so a live Talk/Builder/Metrics
    //    run shades the *other* contended surfaces with a "Surface is busy" overlay (R4 contention). ──

    /// <summary>The section that currently owns the live run (0 Talk · 3 Builder · 4 Metrics), or -1.</summary>
    public int BusyOwnerSection => Talk.IsRunning ? 0 : Builder.IsRunning ? 3 : Metrics.IsRunning ? 4 : -1;

    public string BusyOwnerLabel => BusyOwnerSection switch { 0 => "Talk", 3 => "Builder", 4 => "Metrics", _ => "" };

    /// <summary>The overlay shows on a contended surface (Talk/Builder/Metrics) that is NOT the live owner.</summary>
    public bool IsCurrentSectionBlocked =>
        BusyOwnerSection >= 0 && SelectedSection is 0 or 3 or 4 && SelectedSection != BusyOwnerSection;

    public string BlockedMessage =>
        $"A {BusyOwnerLabel} session is holding the audio device and CPU. One real pipeline runs at a time, so live surfaces block each other.";

    public string GoToOwnerLabel => $"Go to {BusyOwnerLabel}";

    private void NotifyBlockedState()
    {
        OnPropertyChanged(nameof(BusyOwnerSection));
        OnPropertyChanged(nameof(BusyOwnerLabel));
        OnPropertyChanged(nameof(IsCurrentSectionBlocked));
        OnPropertyChanged(nameof(BlockedMessage));
        OnPropertyChanged(nameof(GoToOwnerLabel));
    }

    [RelayCommand]
    private void GoToBusyOwner()
    {
        if (BusyOwnerSection >= 0) SelectedSection = BusyOwnerSection;
    }

    [RelayCommand]
    private void EndBusyOwner()
    {
        // Stop whichever contended surface holds the device (each exposes a CanStop-gated StopCommand).
        if (Talk.IsRunning) Talk.StopCommand.Execute(null);
        else if (Builder.IsRunning) Builder.StopCommand.Execute(null);
        else if (Metrics.IsRunning) Metrics.StopCommand.Execute(null);
    }

    // ── pipeline profiles (the app-wide active-pipeline selector, shown on every page) ──

    /// <summary>Shown when the live config doesn't match a saved profile (e.g. after a Config Apply).</summary>
    public const string CustomProfile = "— Custom —";

    public ObservableCollection<string> PipelineProfiles { get; } = new();

    /// <summary>True once at least one profile is saved (beyond the Custom sentinel) — gates the empty hint.</summary>
    public bool HasPipelineProfiles => PipelineProfiles.Count > 1;

    /// <summary>The active pipeline shown as stage chips in the profile bar (VST-005): VAD · STT · Agent · TTS.</summary>
    public ObservableCollection<ShellChainNode> ActiveChain { get; } = new();

    /// <summary>True when the active config resolves to a chain (the chips show; otherwise the build-hint does).</summary>
    public bool HasActiveChain => ActiveChain.Count > 0;

    private void RefreshActiveChain()
    {
        var voxa = Services.Configuration.GetSection("Voxa");
        var agent = voxa["Agent:Provider"] ?? "OpenAI";
        if (string.Equals(agent, "OpenAI", StringComparison.OrdinalIgnoreCase))
            agent = voxa["Agent:Model"] ?? "gpt-4o-mini";
        var stages = new (string Stage, string? Name)[]
        {
            ("vad", voxa["Vad:Engine"] ?? "Silero"),
            ("stt", voxa["Stt"]),
            ("agent", agent),
            ("tts", voxa["Tts"]),
        };
        var present = stages.Where(s => !string.IsNullOrWhiteSpace(s.Name)).ToList();

        ActiveChain.Clear();
        for (int i = 0; i < present.Count; i++)
            ActiveChain.Add(new ShellChainNode(present[i].Stage, present[i].Name!, ShowSeparator: i < present.Count - 1));
        OnPropertyChanged(nameof(HasActiveChain));
    }

    [ObservableProperty] private string? _selectedPipelineProfile;

    private bool _suppressProfileActivate;

    private void RefreshProfiles()
    {
        _suppressProfileActivate = true; // a programmatic SelectedItem set must not re-activate
        PipelineProfiles.Clear();
        PipelineProfiles.Add(CustomProfile);
        foreach (var name in Services.Profiles.Names) PipelineProfiles.Add(name);
        SelectedPipelineProfile = Services.Profiles.ActiveName ?? CustomProfile;
        _suppressProfileActivate = false;
        OnPropertyChanged(nameof(HasPipelineProfiles));
    }

    partial void OnSelectedPipelineProfileChanged(string? value)
    {
        // Picking a profile applies it app-wide. Custom is a display state, and a rebuild can't run
        // under a live session (mirrors Config.ApplyBlocked) — the bar is disabled then anyway.
        if (_suppressProfileActivate || value is null || value == CustomProfile || IsLive) return;
        Services.ActivateProfile(value);
        // Reload the Builder canvas from the MERGED live config (not the sparse stored pairs, which would
        // show hard-coded defaults for omitted keys and bake them back on Save) — Codex P2.
        Builder.LoadFromLiveConfig();
        RefreshActiveChain();   // the bar chips follow the newly-activated profile
        Toasts.Show(ToastTone.Info, "Active pipeline switched", value);
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        if (SelectedPipelineProfile is not { } name || name == CustomProfile) return;
        var wasActive = string.Equals(Services.Profiles.ActiveName, name, StringComparison.OrdinalIgnoreCase);
        Services.Profiles.Delete(name);
        if (wasActive) Services.ActivateProfile(null); // the live profile was deleted — fall back to base config
        Toasts.Show(ToastTone.Info, "Profile deleted", name);
    }

    partial void OnSelectedSectionChanged(int value)
    {
        NotifyBlockedState();   // the overlay depends on the current section vs. the busy owner

        // Entering cache-dependent views refreshes their state (downloads may have happened);
        // entering Metrics rescans the bundle folder (runs may have landed from another session).
        if (value == 1) Playgrounds.RefreshCacheState();
        if (value == 2) Voices.Refresh();
        if (value == 3) Builder.RefreshCacheState();
        if (value == 4) Metrics.RefreshRuns();
        if (value == 5) Models.Refresh();
        // Opening Config is a user action — only now may a cloud provider's voices load over the
        // network (the ctor deliberately doesn't, to honour "no network before the user acts").
        if (value == 6) Config.RefreshCloudVoices();
    }
}
