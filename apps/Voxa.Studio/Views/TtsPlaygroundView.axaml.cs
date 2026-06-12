using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Voxa.Studio.ViewModels;

namespace Voxa.Studio.Views;

public partial class TtsPlaygroundView : UserControl
{
    // Playback position is wall-clock-derived; ~30 fps is the repo's render-side coalescing law.
    private readonly DispatcherTimer _playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };

    public TtsPlaygroundView()
    {
        InitializeComponent();
        _playbackTimer.Tick += (_, _) => (DataContext as TtsPlaygroundViewModel)?.UpdatePlayback();
        Scrubber.SeekRequested += async (_, position) =>
        {
            if (DataContext is TtsPlaygroundViewModel { CurrentTake: { } take } vm)
                await vm.PlayFromAsync(take, position);
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _playbackTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _playbackTimer.Stop();
        base.OnDetachedFromVisualTree(e);
    }
}
