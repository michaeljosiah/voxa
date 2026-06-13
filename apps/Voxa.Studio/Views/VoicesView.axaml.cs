using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Voxa.Speech.Voices;
using Voxa.Studio.ViewModels;

namespace Voxa.Studio.Views;

public partial class VoicesView : UserControl
{
    public VoicesView() => InitializeComponent();

    private VoicesViewModel? Vm => DataContext as VoicesViewModel;

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Pull the library live when the section opens (the shell also calls Refresh on nav-in).
        Vm?.Refresh();
    }

    private async void OnAddSample(object? sender, RoutedEventArgs e)
    {
        if (Vm is null || TopLevel.GetTopLevel(this) is not { } top) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add a reference clip for the clone",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Audio") { Patterns = ["*.wav", "*.mp3", "*.flac", "*.m4a"] }],
        });
        foreach (var file in files)
        {
            if (file.TryGetLocalPath() is not { } path) continue;
            var bytes = await File.ReadAllBytesAsync(path);
            Vm.AddSample(new VoiceSample(Path.GetFileName(path), bytes, MimeFor(path)));
        }
    }

    private static string MimeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg",
        ".flac" => "audio/flac",
        ".m4a" => "audio/mp4",
        _ => "audio/wav",
    };
}
