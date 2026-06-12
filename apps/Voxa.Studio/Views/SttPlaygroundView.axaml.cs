using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Voxa.Studio.ViewModels;

namespace Voxa.Studio.Views;

public partial class SttPlaygroundView : UserControl
{
    public SttPlaygroundView()
    {
        InitializeComponent();
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private SttPlaygroundViewModel? Vm => DataContext as SttPlaygroundViewModel;

    private void OnSourceChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { IsChecked: true } radio || Vm is null) return;
        Vm.Source = radio.Name switch
        {
            nameof(SrcFile) => SttSource.File,
            nameof(SrcMic) => SttSource.Mic,
            _ => SttSource.Fixture,
        };
    }

    private async void OnBrowseClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is null || TopLevel.GetTopLevel(this) is not { } top) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Pick a WAV file",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("WAV audio") { Patterns = ["*.wav"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            Vm.FilePath = path;
    }

    /// <summary>Dropping a WAV anywhere on the lab selects the file source and loads it.</summary>
    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (Vm is null) return;
        var path = e.Data.GetFiles()?.FirstOrDefault()?.TryGetLocalPath();
        if (path is null || !path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) return;
        Vm.Source = SttSource.File;
        Vm.FilePath = path;
    }
}
