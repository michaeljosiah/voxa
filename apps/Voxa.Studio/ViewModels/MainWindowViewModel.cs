using CommunityToolkit.Mvvm.ComponentModel;
using Voxa.Studio.Services;

namespace Voxa.Studio.ViewModels;

/// <summary>
/// Shell view model: the nav rail's four sections and the shared session coordination
/// (one audio device — playground playback/capture disables while a Talk session is live).
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    public MainWindowViewModel(StudioServices services)
    {
        Services = services;
        Talk = new TalkViewModel(services);
        Playgrounds = new PlaygroundsViewModel(services);
        Models = new ModelsViewModel(services);
        Config = new ConfigViewModel(services);

        Talk.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TalkViewModel.IsRunning))
            {
                Playgrounds.Tts.PlaybackBlocked = Talk.IsRunning;
                Playgrounds.Stt.CaptureBlocked = Talk.IsRunning;
                Config.ApplyBlocked = Talk.IsRunning; // a live session's scope belongs to the old container
                OnPropertyChanged(nameof(IsLive));
            }
        };

        // Config "Apply" rebuilt the container — every view re-reads from it.
        services.Reconfigured += () =>
        {
            Talk.RefreshFromConfig();
            Playgrounds.RefreshCacheState();
            Models.Refresh();
        };
    }

    public StudioServices Services { get; }
    public TalkViewModel Talk { get; }
    public PlaygroundsViewModel Playgrounds { get; }
    public ModelsViewModel Models { get; }
    public ConfigViewModel Config { get; }

    /// <summary>0 Talk, 1 Playgrounds, 2 Models, 3 Config — bound to the nav rail.</summary>
    [ObservableProperty] private int _selectedSection;

    public bool IsLive => Talk.IsRunning;

    partial void OnSelectedSectionChanged(int value)
    {
        // Entering Playgrounds/Models refreshes cache-dependent state (downloads may have happened).
        if (value == 1) Playgrounds.RefreshCacheState();
        if (value == 2) Models.Refresh();
    }
}
