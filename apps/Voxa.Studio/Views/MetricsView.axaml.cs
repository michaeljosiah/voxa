using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Voxa.Studio.ViewModels;

namespace Voxa.Studio.Views;

public partial class MetricsView : UserControl
{
    private readonly DispatcherTimer _timer;

    public MetricsView()
    {
        InitializeComponent();
        // The TalkView drain pattern: hub events buffer in the VM; this timer applies them.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => Vm?.DrainPending();
    }

    private MetricsViewModel? Vm => DataContext as MetricsViewModel;

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
    }

    private void OnSourceChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { IsChecked: true } radio || Vm is null) return;
        Vm.SourceIndex = radio.Name switch
        {
            nameof(SrcWav) => 1,
            nameof(SrcMic) => 2,
            _ => 0,
        };
    }

    private async void OnAddScriptFilesClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is null || TopLevel.GetTopLevel(this) is not { } top) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add utterance WAVs to the deck",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("WAV audio") { Patterns = ["*.wav"] }],
        });
        Vm.AddScriptFiles(files.Select(f => f.TryGetLocalPath()).Where(p => p is not null)!);
    }

    private async void OnBrowseWavClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is null || TopLevel.GetTopLevel(this) is not { } top) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Pick an utterance WAV",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("WAV audio") { Patterns = ["*.wav"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            Vm.WavPath = path;
    }
}
