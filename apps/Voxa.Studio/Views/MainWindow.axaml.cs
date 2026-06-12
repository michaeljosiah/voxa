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
            "Voices" => 1,
            "Models" => 2,
            "Config" => 3,
            _ => 0,
        };

        if (DataContext is MainWindowViewModel vm)
            vm.SelectedSection = section;

        TalkHost.IsVisible = section == 0;
        VoicesHost.IsVisible = section == 1;
        ModelsHost.IsVisible = section == 2;
        ConfigHost.IsVisible = section == 3;
    }
}
