using CommunityToolkit.Mvvm.ComponentModel;
using Voxa.Studio.Services;

namespace Voxa.Studio.ViewModels;

/// <summary>
/// Shell view model: the nav rail's four sections and the shared session coordination
/// (one output device — Voice Lab playback disables while a Talk session is live).
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    public MainWindowViewModel(StudioServices services)
    {
        Services = services;
        Talk = new TalkViewModel(services);
        Voices = new VoicesViewModel(services);
        Models = new ModelsViewModel(services);
        Config = new ConfigViewModel(services);

        Talk.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TalkViewModel.IsRunning))
            {
                Voices.PlaybackBlocked = Talk.IsRunning;
                Config.ApplyBlocked = Talk.IsRunning; // a live session's scope belongs to the old container
                OnPropertyChanged(nameof(IsLive));
            }
        };

        // Config "Apply" rebuilt the container — every view re-reads from it.
        services.Reconfigured += () =>
        {
            Talk.RefreshFromConfig();
            Voices.RefreshCacheState();
            Models.Refresh();
        };
    }

    public StudioServices Services { get; }
    public TalkViewModel Talk { get; }
    public VoicesViewModel Voices { get; }
    public ModelsViewModel Models { get; }
    public ConfigViewModel Config { get; }

    /// <summary>0 Talk, 1 Voices, 2 Models, 3 Config — bound to the nav rail.</summary>
    [ObservableProperty] private int _selectedSection;

    public bool IsLive => Talk.IsRunning;

    partial void OnSelectedSectionChanged(int value)
    {
        // Entering Voices/Models refreshes cache-dependent state (downloads may have happened).
        if (value == 1) Voices.RefreshCacheState();
        if (value == 2) Models.Refresh();
    }
}
