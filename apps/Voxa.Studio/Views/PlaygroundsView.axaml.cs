using Avalonia.Controls;
using Avalonia.Interactivity;
using Voxa.Studio.ViewModels;

namespace Voxa.Studio.Views;

public partial class PlaygroundsView : UserControl
{
    public PlaygroundsView() => InitializeComponent();

    private void OnLabChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { IsChecked: true } radio) return;
        var lab = radio.Name == nameof(LabTts) ? 1 : 0;
        if (DataContext is PlaygroundsViewModel vm)
            vm.SelectedLab = lab;
        SttHost.IsVisible = lab == 0;
        TtsHost.IsVisible = lab == 1;
    }
}
