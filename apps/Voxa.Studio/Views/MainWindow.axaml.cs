using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Voxa.Studio.ViewModels;

namespace Voxa.Studio.Views;

public partial class MainWindow : Window
{
    // Titlebar session clock: a view concern (the VM stays Avalonia-free). Runs only while
    // a Talk session is live — the timer text is data-backed, per the motion rules.
    private readonly Stopwatch _session = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

    public MainWindow()
    {
        InitializeComponent();

        _clock.Tick += (_, _) =>
            SessionClock.Text = _session.Elapsed.ToString(@"mm\:ss");

        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainWindowViewModel vm) return;
            vm.PropertyChanged += (_, e) =>
            {
                // Cross-navigation (Config's "Open in Builder") changes SelectedSection from
                // the VM side — reflect it onto the rail, which drives host visibility.
                if (e.PropertyName == nameof(MainWindowViewModel.SelectedSection))
                {
                    var radio = vm.SelectedSection switch
                    {
                        1 => NavPlaygrounds,
                        2 => NavVoices,
                        3 => NavBuilder,
                        4 => NavMetrics,
                        5 => NavModels,
                        6 => NavConfig,
                        7 => NavDiarization,
                        _ => NavTalk,
                    };
                    radio.IsChecked = true;
                    return;
                }

                if (e.PropertyName != nameof(MainWindowViewModel.IsLive)) return;
                if (vm.IsLive)
                {
                    _session.Restart();
                    SessionClock.Text = "00:00";
                    _clock.Start();
                }
                else
                {
                    _clock.Stop();
                    _session.Stop();
                }
            };
        };
    }

    /// <summary>
    /// Open the Settings dialog (VST-003). Showing it is a View concern — <c>ShowDialog</c> needs the
    /// owner window, which lives here (like the session clock). A fresh <see cref="SettingsViewModel"/>
    /// works over the shared secrets service; on Save the shell applies the new credentials.
    /// </summary>
    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var settings = new SettingsViewModel(vm.Services.Secrets);
        await new SettingsDialog { DataContext = settings }.ShowDialog(this);

        if (settings.Saved) vm.OnSettingsSaved();
    }

    private void OnNavChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { IsChecked: true } radio) return;

        var section = radio.Tag switch
        {
            "Playgrounds" => 1,
            "Voices" => 2,
            "Builder" => 3,
            "Metrics" => 4,
            "Models" => 5,
            "Config" => 6,
            "Diarization" => 7,
            _ => 0,
        };

        if (DataContext is MainWindowViewModel vm)
            vm.SelectedSection = section;

        TalkHost.IsVisible = section == 0;
        PlaygroundsHost.IsVisible = section == 1;
        VoicesHost.IsVisible = section == 2;
        BuilderHost.IsVisible = section == 3;
        MetricsHost.IsVisible = section == 4;
        ModelsHost.IsVisible = section == 5;
        ConfigHost.IsVisible = section == 6;
        DiarizationHost.IsVisible = section == 7;
    }
}
