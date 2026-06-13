using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Voxa.Studio.Services;

namespace Voxa.Studio.Controls;

/// <summary>
/// Per-turn stage stacks (VST-002 §9.2): one vertical bar per completed turn, stage blocks
/// stacked in pipeline order, height proportional to milliseconds. The chart the takeaway line
/// reads from — the dominant color band is the dominant stage.
/// </summary>
public sealed class RunStacksControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<RunTurn>?> TurnsProperty =
        AvaloniaProperty.Register<RunStacksControl, IReadOnlyList<RunTurn>?>(nameof(Turns));

    static RunStacksControl()
    {
        AffectsRender<RunStacksControl>(TurnsProperty);
    }

    public IReadOnlyList<RunTurn>? Turns
    {
        get => GetValue(TurnsProperty);
        set => SetValue(TurnsProperty, value);
    }

    private const double MaxBarWidth = 26;
    private const double Gap = 6;
    private const double AxisPad = 16; // room for turn numbers under the bars

    public override void Render(DrawingContext context)
    {
        var turns = Turns;
        var w = Bounds.Width;
        var h = Bounds.Height - AxisPad;
        if (turns is null || turns.Count == 0 || w <= 0 || h <= 20) return;

        var maxMs = Math.Max(turns.Max(t => t.TotalMs), 0.001);
        var barWidth = Math.Min(MaxBarWidth, (w - (turns.Count - 1) * Gap) / turns.Count);
        if (barWidth < 2) barWidth = 2;

        // The scale label: what full height means.
        var scale = new FormattedText(
            $"{maxMs:F0} ms", System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, StagePalette.Mono, 10, StagePalette.Label);
        context.DrawText(scale, new Point(0, 0));

        // Label every bar when they fit, else every k-th.
        var labelEvery = barWidth >= 16 ? 1 : Math.Max(1, (int)Math.Ceiling(16 / (barWidth + Gap)));

        double x = 0;
        foreach (var turn in turns)
        {
            var y = h;
            foreach (var stage in RunStats.StageOrder)
            {
                if (!turn.Stages.TryGetValue(stage, out var ms) || ms <= 0) continue;
                var blockH = Math.Max(ms / maxMs * h, 1);
                y -= blockH;
                context.DrawRectangle(StagePalette.For(stage), null, new Rect(x, y, barWidth, blockH));
            }

            if ((turn.Number - 1) % labelEvery == 0)
            {
                var label = new FormattedText(
                    turn.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, StagePalette.Mono, 9, StagePalette.Label);
                context.DrawText(label, new Point(x + (barWidth - label.Width) / 2, h + 3));
            }

            x += barWidth + Gap;
            if (x > w) break; // more turns than pixels — the CSV has the rest
        }
    }
}
