using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Voxa.Studio.Controls;

/// <summary>
/// The signature bottom-aligned waveform bars (VST-002 §3 motif), hand-drawn like the other
/// Studio charts. Two jobs: a static amplitude envelope above STT transcript cards, and the TTS
/// playback scrubber — <see cref="Position"/> splits played (cyan) from unplayed (ink) bars and
/// clicking/dragging seeks when <see cref="IsInteractive"/>. Data-backed only: no levels, no bars.
/// </summary>
public sealed class WaveformStripControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<float>?> LevelsProperty =
        AvaloniaProperty.Register<WaveformStripControl, IReadOnlyList<float>?>(nameof(Levels));

    /// <summary>Playhead 0..1; negative hides it (plain envelope mode).</summary>
    public static readonly StyledProperty<double> PositionProperty =
        AvaloniaProperty.Register<WaveformStripControl, double>(nameof(Position), -1);

    public static readonly StyledProperty<bool> IsInteractiveProperty =
        AvaloniaProperty.Register<WaveformStripControl, bool>(nameof(IsInteractive));

    static WaveformStripControl()
    {
        AffectsRender<WaveformStripControl>(LevelsProperty, PositionProperty, IsInteractiveProperty);
    }

    public IReadOnlyList<float>? Levels
    {
        get => GetValue(LevelsProperty);
        set => SetValue(LevelsProperty, value);
    }

    public double Position
    {
        get => GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    public bool IsInteractive
    {
        get => GetValue(IsInteractiveProperty);
        set => SetValue(IsInteractiveProperty, value);
    }

    /// <summary>Raised with the 0..1 position the user clicked/dragged to (interactive mode).</summary>
    public event EventHandler<double>? SeekRequested;

    private static readonly IBrush PlayedBrush = new SolidColorBrush(Color.Parse("#4FC3F7"));   // pulse-400
    private static readonly IBrush RestingBrush = new SolidColorBrush(Color.Parse("#2C3645"));  // ink-600
    private static readonly IBrush PlayheadBrush = new SolidColorBrush(Color.Parse("#EAF1F8")); // text-1

    private bool _dragging;

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        var levels = Levels;
        if (w <= 0 || h <= 0 || levels is null || levels.Count == 0) return;

        const double gap = 2;
        var barWidth = Math.Max(1, (w - gap * (levels.Count - 1)) / levels.Count);
        var step = barWidth + gap;
        var position = Position;
        var playheadX = position >= 0 ? Math.Clamp(position, 0, 1) * w : -1;

        for (int i = 0; i < levels.Count; i++)
        {
            var x = i * step;
            // Floor of 8% keeps silence visible as a hairline of bars, not a blank.
            var barHeight = Math.Max(h * 0.08, h * Math.Clamp(levels[i], 0f, 1f));
            var brush = playheadX >= 0 && x + barWidth / 2 <= playheadX ? PlayedBrush : RestingBrush;
            context.DrawRectangle(brush, null, new RoundedRect(
                new Rect(x, h - barHeight, barWidth, barHeight), barWidth / 2));
        }

        if (playheadX >= 0)
            context.DrawRectangle(PlayheadBrush, null, new Rect(playheadX - 0.75, 0, 1.5, h));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsInteractive) return;
        _dragging = true;
        Seek(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging) Seek(e.GetPosition(this).X);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
    }

    private void Seek(double x)
    {
        if (Bounds.Width <= 0) return;
        SeekRequested?.Invoke(this, Math.Clamp(x / Bounds.Width, 0, 1));
    }
}
