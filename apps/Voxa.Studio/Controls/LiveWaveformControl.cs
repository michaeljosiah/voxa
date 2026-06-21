using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Voxa.Studio.Controls;

/// <summary>
/// The decorative bouncing-bars waveform in the Talk aside (VST-005 strict-1:1). Unlike
/// <see cref="WaveformStripControl"/> (which renders real captured/synth levels) this is a pure
/// "something is happening" indicator: when <see cref="IsLive"/> the bars bounce on an internal
/// ~16 fps timer; otherwise they rest as a muted hairline. No data binding, no allocations per frame.
/// </summary>
public sealed class LiveWaveformControl : Control
{
    public static readonly StyledProperty<bool> IsLiveProperty =
        AvaloniaProperty.Register<LiveWaveformControl, bool>(nameof(IsLive));

    public static readonly StyledProperty<int> BarsProperty =
        AvaloniaProperty.Register<LiveWaveformControl, int>(nameof(Bars), 20);

    public bool IsLive
    {
        get => GetValue(IsLiveProperty);
        set => SetValue(IsLiveProperty, value);
    }

    public int Bars
    {
        get => GetValue(BarsProperty);
        set => SetValue(BarsProperty, value);
    }

    private static readonly IBrush LiveBrush = new SolidColorBrush(Color.Parse("#4FC3F7"));   // pulse-400
    private static readonly IBrush RestBrush = new SolidColorBrush(Color.Parse("#3A4656"));   // ink-500

    private readonly DispatcherTimer _timer;
    private double _phase;

    public LiveWaveformControl()
    {
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(60), DispatcherPriority.Background, (_, _) =>
        {
            _phase += 0.35;
            InvalidateVisual();
        });
    }

    static LiveWaveformControl()
    {
        AffectsRender<LiveWaveformControl>(IsLiveProperty, BarsProperty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsLiveProperty) SyncTimer();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SyncTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();   // never leave a timer running once we leave the tree
    }

    private void SyncTimer()
    {
        if (IsLive && this.GetVisualRoot() is not null) _timer.Start();
        else _timer.Stop();
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        var count = Math.Max(1, Bars);
        if (w <= 0 || h <= 0) return;

        const double gap = 3;
        var barWidth = Math.Max(1.5, (w - gap * (count - 1)) / count);
        var step = barWidth + gap;
        var brush = IsLive ? LiveBrush : RestBrush;

        for (int i = 0; i < count; i++)
        {
            // Live: a travelling sine per bar (0.35..1 of height); rest: a flat low hairline.
            var amp = IsLive ? 0.35 + 0.65 * (0.5 + 0.5 * Math.Sin(_phase + i * 0.55)) : 0.18;
            var barHeight = Math.Max(barWidth, h * amp);
            var x = i * step;
            var y = (h - barHeight) / 2;   // centre-anchored bounce
            context.DrawRectangle(brush, null, new RoundedRect(
                new Rect(x, y, barWidth, barHeight), barWidth / 2));
        }
    }
}
