using Avalonia.Controls;
using Avalonia.Interactivity;
using Voxa.Studio.ViewModels;

namespace Voxa.Studio.Views;

public partial class ConfigView : UserControl
{
    public ConfigView()
    {
        InitializeComponent();
    }

    private async void OnCopyClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ConfigViewModel vm) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(vm.ExportJson);
    }
}
