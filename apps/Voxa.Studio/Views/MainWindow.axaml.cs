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
                        2 => NavBuilder,
                        3 => NavModels,
                        4 => NavConfig,
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

    private void OnNavChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { IsChecked: true } radio) return;

        var section = radio.Tag switch
        {
            "Playgrounds" => 1,
            "Builder" => 2,
            "Models" => 3,
            "Config" => 4,
            _ => 0,
        };

        if (DataContext is MainWindowViewModel vm)
            vm.SelectedSection = section;

        TalkHost.IsVisible = section == 0;
        PlaygroundsHost.IsVisible = section == 1;
        BuilderHost.IsVisible = section == 2;
        ModelsHost.IsVisible = section == 3;
        ConfigHost.IsVisible = section == 4;
    }
}
