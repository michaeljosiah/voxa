using CommunityToolkit.Mvvm.ComponentModel;
using Voxa.Studio.Services;

namespace Voxa.Studio.ViewModels;

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
        Builder = new BuilderViewModel(services);
        Metrics = new MetricsViewModel(services);
        Models = new ModelsViewModel(services);
        Config = new ConfigViewModel(services);

        // One audio device — and one set of cores: a concurrent session would also make every
        // Metrics number a lie (the R4 contention rule), so live surfaces block each other.
        Talk.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TalkViewModel.IsRunning)) SyncLiveState();
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
            SelectedSection = 2;
        };

        // Talk's waterfall deep-link: land on that stage's series in the workbench (§5).
        Talk.OpenInMetricsRequested += stage =>
        {
            Metrics.FocusStage(stage);
            SelectedSection = 3;
        };

        // Config "Apply" rebuilt the container — every view re-reads from it.
        services.Reconfigured += () =>
        {
            Talk.RefreshFromConfig();
            Playgrounds.RefreshCacheState();
            Models.Refresh();
        };
    }

    private void SyncLiveState()
    {
        var live = Talk.IsRunning || Builder.IsRunning || Metrics.IsRunning;
        Playgrounds.Tts.PlaybackBlocked = live;
        Playgrounds.Stt.CaptureBlocked = live;
        Config.ApplyBlocked = Talk.IsRunning; // a live Talk session's scope belongs to the old container
        Builder.RunBlocked = Talk.IsRunning || Metrics.IsRunning;
        Talk.StartBlocked = Builder.IsRunning || Metrics.IsRunning;
        Metrics.RunBlocked = Talk.IsRunning || Builder.IsRunning;
        OnPropertyChanged(nameof(IsLive));
    }

    public StudioServices Services { get; }
    public TalkViewModel Talk { get; }
    public PlaygroundsViewModel Playgrounds { get; }
    public BuilderViewModel Builder { get; }
    public MetricsViewModel Metrics { get; }
    public ModelsViewModel Models { get; }
    public ConfigViewModel Config { get; }

    /// <summary>0 Talk, 1 Playgrounds, 2 Builder, 3 Metrics, 4 Models, 5 Config — the nav rail.</summary>
    [ObservableProperty] private int _selectedSection;

    public bool IsLive => Talk.IsRunning || Builder.IsRunning || Metrics.IsRunning;

    partial void OnSelectedSectionChanged(int value)
    {
        // Entering cache-dependent views refreshes their state (downloads may have happened);
        // entering Metrics rescans the bundle folder (runs may have landed from another session).
        if (value == 1) Playgrounds.RefreshCacheState();
        if (value == 2) Builder.RefreshCacheState();
        if (value == 3) Metrics.RefreshRuns();
        if (value == 4) Models.Refresh();
    }
}
