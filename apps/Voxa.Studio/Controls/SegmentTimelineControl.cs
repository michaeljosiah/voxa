using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Voxa.Studio.ViewModels;

namespace Voxa.Studio.Controls;

/// <summary>
/// A speech-activity timeline: a track spanning the whole clip with a filled block for every speech
/// region, width and position proportional to its share of the duration. The visual sibling of
/// <see cref="WaterfallControl"/> — pure <see cref="Render"/>, redraws when its data changes.
/// </summary>
public sealed class SegmentTimelineControl : Control
{
    public static readonly StyledProperty<SpeechTimeline?> TimelineProperty =
        AvaloniaProperty.Register<SegmentTimelineControl, SpeechTimeline?>(nameof(Timeline));

    static SegmentTimelineControl() => AffectsRender<SegmentTimelineControl>(TimelineProperty);

    public SpeechTimeline? Timeline
    {
        get => GetValue(TimelineProperty);
        set => SetValue(TimelineProperty, value);
    }

    private static readonly IBrush TrackBrush = new SolidColorBrush(Color.Parse("#16202E"));
    private static readonly IBrush SpeechBrush = new SolidColorBrush(Color.Parse("#4FC3F7"));
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#76849B"));
    private static readonly Typeface LabelFace = new("Cascadia Code, Consolas, monospace");

    private const double LabelRow = 14;

    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        var trackH = Math.Max(h - LabelRow, 8);

        // The full-duration track (silence) under the speech blocks.
        context.DrawRectangle(TrackBrush, null, new RoundedRect(new Rect(0, 0, w, trackH), 4));

        var timeline = Timeline;
        if (timeline is null || timeline.TotalSeconds <= 0) return;

        var total = timeline.TotalSeconds;
        const double minWidth = 2; // a very short region still gets a visible sliver
        foreach (var span in timeline.Speech)
        {
            var x = span.StartSeconds / total * w;
            var width = Math.Max(span.DurationSeconds / total * w, minWidth);
            if (x > w) continue;
            context.DrawRectangle(SpeechBrush, null,
                new RoundedRect(new Rect(x, 0, Math.Min(width, w - x), trackH), 3));
        }

        // End-cap time ticks: 0s on the left, the clip length on the right.
        context.DrawText(Tick("0s"), new Point(0, trackH + 2));
        var end = Tick($"{total:F1}s");
        context.DrawText(end, new Point(Math.Max(0, w - end.Width), trackH + 2));
    }

    private static FormattedText Tick(string text) => new(
        text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelFace, 10, LabelBrush);
}
