using Avalonia.Controls;
using Avalonia.Interactivity;
using Voxa.Studio.ViewModels;

namespace Voxa.Studio.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
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
