using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Voxa.Studio.Services;

namespace Voxa.Studio.Controls;

/// <summary>
/// Per-stage latency trend over a run's turns (VST-002 §9.2): one polyline per stage in the
/// stage palette. When <see cref="FocusedStage"/> is set (Talk's waterfall deep-link lands
/// here), that series draws emphasized and the rest recede — the chart answers "is THIS stage
/// drifting?" without hiding the context.
/// </summary>
public sealed class StageTrendControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<RunTurn>?> TurnsProperty =
        AvaloniaProperty.Register<StageTrendControl, IReadOnlyList<RunTurn>?>(nameof(Turns));

    public static readonly StyledProperty<string?> FocusedStageProperty =
        AvaloniaProperty.Register<StageTrendControl, string?>(nameof(FocusedStage));

    static StageTrendControl()
    {
        AffectsRender<StageTrendControl>(TurnsProperty, FocusedStageProperty);
    }

    public IReadOnlyList<RunTurn>? Turns
    {
        get => GetValue(TurnsProperty);
        set => SetValue(TurnsProperty, value);
    }

    public string? FocusedStage
    {
        get => GetValue(FocusedStageProperty);
        set => SetValue(FocusedStageProperty, value);
    }

    private const double LegendPad = 52; // right gutter for the series labels

    public override void Render(DrawingContext context)
    {
        var turns = Turns;
        var w = Bounds.Width - LegendPad;
        var h = Bounds.Height;
        if (turns is null || turns.Count == 0 || w <= 10 || h <= 10) return;

        var maxMs = Math.Max(
            turns.SelectMany(t => t.Stages.Values).DefaultIfEmpty(0).Max(), 0.001);

        var scale = new FormattedText(
            $"{maxMs:F0} ms", System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, StagePalette.Mono, 10, StagePalette.Label);
        context.DrawText(scale, new Point(0, 0));

        var focused = FocusedStage;
        foreach (var stage in RunStats.StageOrder)
        {
            var points = new List<Point>();
            for (var i = 0; i < turns.Count; i++)
            {
                if (!turns[i].Stages.TryGetValue(stage, out var ms)) continue;
                var x = turns.Count == 1 ? w / 2 : i / (double)(turns.Count - 1) * w;
                var y = h - ms / maxMs * (h - 14) - 2; // top inset clears the scale label
                points.Add(new Point(x, y));
            }
            if (points.Count == 0) continue;

            var isFocused = focused is null || focused == stage;
            var brush = StagePalette.For(stage);
            var pen = new Pen(brush, focused == stage ? 2.0 : 1.0)
            {
                // With a focus set, the other series recede but stay legible.
                Brush = isFocused ? brush : new SolidColorBrush(((SolidColorBrush)brush).Color, 0.30),
            };

            if (points.Count == 1)
            {
                context.DrawEllipse(pen.Brush, null, points[0], 2.5, 2.5);
            }
            else
            {
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    g.BeginFigure(points[0], isFilled: false);
                    for (var i = 1; i < points.Count; i++) g.LineTo(points[i]);
                    g.EndFigure(false);
                }
                context.DrawGeometry(null, pen, geometry);
            }

            // Series label in the right gutter at the line's last height.
            var label = new FormattedText(
                StagePalette.LabelFor(stage), System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, StagePalette.Mono, 9,
                isFocused ? brush : StagePalette.Label);
            context.DrawText(label, new Point(w + 6, Math.Clamp(points[^1].Y - 5, 0, h - 12)));
        }
    }
}
