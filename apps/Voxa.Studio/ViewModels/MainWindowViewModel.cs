using CommunityToolkit.Mvvm.ComponentModel;
using Voxa.Studio.Services;

namespace Voxa.Studio.ViewModels;

/// <summary>
/// Shell view model: the nav rail's five sections and the shared session coordination
/// (one audio device — playground playback/capture and Builder/Talk runs disable each other
/// while a session is live).
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    public MainWindowViewModel(StudioServices services)
    {
        Services = services;
        Talk = new TalkViewModel(services);
        Playgrounds = new PlaygroundsViewModel(services);
        Builder = new BuilderViewModel(services);
        Models = new ModelsViewModel(services);
        Config = new ConfigViewModel(services);

        // One audio device: whichever surface is live (Talk or a Builder run) owns it.
        Talk.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TalkViewModel.IsRunning)) SyncLiveState();
        };
        Builder.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BuilderViewModel.IsRunning)) SyncLiveState();
        };

        // Config's "Open in Builder": the current draft becomes a graph (§5 cross-navigation).
        Config.OpenInBuilderRequested += () =>
        {
            Builder.SeedFromPairs(Config.DraftPairs(includeSecrets: true));
            SelectedSection = 2;
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
        var live = Talk.IsRunning || Builder.IsRunning;
        Playgrounds.Tts.PlaybackBlocked = live;
        Playgrounds.Stt.CaptureBlocked = live;
        Config.ApplyBlocked = Talk.IsRunning; // a live Talk session's scope belongs to the old container
        Builder.RunBlocked = Talk.IsRunning;
        Talk.StartBlocked = Builder.IsRunning;
        OnPropertyChanged(nameof(IsLive));
    }

    public StudioServices Services { get; }
    public TalkViewModel Talk { get; }
    public PlaygroundsViewModel Playgrounds { get; }
    public BuilderViewModel Builder { get; }
    public ModelsViewModel Models { get; }
    public ConfigViewModel Config { get; }

    /// <summary>0 Talk, 1 Playgrounds, 2 Builder, 3 Models, 4 Config — bound to the nav rail.</summary>
    [ObservableProperty] private int _selectedSection;

    public bool IsLive => Talk.IsRunning || Builder.IsRunning;

    partial void OnSelectedSectionChanged(int value)
    {
        // Entering cache-dependent views refreshes their state (downloads may have happened).
        if (value == 1) Playgrounds.RefreshCacheState();
        if (value == 2) Builder.RefreshCacheState();
        if (value == 3) Models.Refresh();
    }
}
