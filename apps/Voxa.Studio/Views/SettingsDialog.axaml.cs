using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Voxa.Studio.Services;
using Voxa.Studio.ViewModels;

namespace Voxa.Studio.Views;

/// <summary>
/// The Settings dialog (VST-003 WS5). Pure presentation over <see cref="SettingsViewModel"/>: the
/// list, the per-provider field editors, and the Add-provider flyout. Save/Cancel set the VM's
/// <see cref="SettingsViewModel.Saved"/> flag and close; the shell decides what to do on Save.
/// </summary>
public partial class SettingsDialog : Window
{
    public SettingsDialog() => InitializeComponent();

    private SettingsViewModel? Vm => DataContext as SettingsViewModel;

    // Borderless modal: the top bar is the drag handle (the close button is a sibling on top, so
    // clicking it doesn't start a drag).
    private void OnDragMove(object? sender, PointerPressedEventArgs e) => BeginMoveDrag(e);

    private void OnAddProviderCard(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ProviderManifest manifest })
            Vm?.Providers.AddProvider(manifest.Name);
        AddProviderButton?.Flyout?.Hide();
    }

    private void OnRemoveProvider(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ProviderRowViewModel row })
            Vm?.Providers.RemoveProvider(row);
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        Vm?.Save();
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Vm?.Cancel();
        Close();
    }
}
