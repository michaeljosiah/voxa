using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Voxa.Studio.ViewModels;

namespace Voxa.Studio.Views;

public partial class DiarizationView : UserControl
{
    public DiarizationView() => InitializeComponent();

    private DiarizationViewModel? Vm => DataContext as DiarizationViewModel;

    /// <summary>Pick a WAV file to segment — the file dialog is a View concern (needs the TopLevel).</summary>
    private async void OnBrowseClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is null || TopLevel.GetTopLevel(this) is not { } top) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Pick a WAV file to segment",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("WAV audio") { Patterns = ["*.wav"] }],
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            Vm.UseFixture = false;
            Vm.FilePath = path;
        }
    }
}
