using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Voxa.Studio.ViewModels;

namespace Voxa.Studio.Controls;

/// <summary>
/// One turn's stage-latency waterfall (VST-001 WS2): a stacked horizontal bar — one colored
/// block per measured stage, width proportional to its share of the turn — with stage + ms
/// labels underneath. Hover any block for the exact number (tooltip set per draw is overkill;
/// the labels carry the numbers).
/// </summary>
public sealed class WaterfallControl : Control
{
    public static readonly StyledProperty<TurnWaterfall?> WaterfallProperty =
        AvaloniaProperty.Register<WaterfallControl, TurnWaterfall?>(nameof(Waterfall));

    static WaterfallControl()
    {
        AffectsRender<WaterfallControl>(WaterfallProperty);
    }

    public TurnWaterfall? Waterfall
    {
        get => GetValue(WaterfallProperty);
        set => SetValue(WaterfallProperty, value);
    }

    private const double BarHeight = 12;
    private const double Gap = 2;

    private static readonly Dictionary<string, IBrush> StageBrushes = new()
    {
        ["vad_close"] = new SolidColorBrush(Color.Parse("#76849B")),
        ["stt_final"] = new SolidColorBrush(Color.Parse("#4FC3F7")),
        ["agent_first_token"] = new SolidColorBrush(Color.Parse("#CE93D8")),
        ["tts_first_byte"] = new SolidColorBrush(Color.Parse("#FFB74D")),
        ["audio_out"] = new SolidColorBrush(Color.Parse("#66BB6A")),
    };
    private static readonly IBrush FallbackBrush = new SolidColorBrush(Color.Parse("#4FC3F7"));
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#76849B"));
    private static readonly Typeface LabelFace = new("Cascadia Code, Consolas, monospace");

    public override void Render(DrawingContext context)
    {
        var waterfall = Waterfall;
        var w = Bounds.Width;
        if (waterfall is null || waterfall.Segments.Count == 0 || w <= 0) return;

        var total = Math.Max(waterfall.TotalMs, 0.001);
        double x = 0;
        // Sub-millisecond stages still get a sliver you can see.
        const double minBlock = 3;

        foreach (var segment in waterfall.Segments)
        {
            var share = segment.Ms / total;
            var width = Math.Max(share * (w - (waterfall.Segments.Count - 1) * Gap), minBlock);
            var brush = StageBrushes.GetValueOrDefault(segment.Stage, FallbackBrush);

            context.DrawRectangle(brush, null, new RoundedRect(new Rect(x, 0, width, BarHeight), 3));

            // Label under wide-enough blocks: "STT 312 ms".
            if (width >= 46)
            {
                var text = new FormattedText(
                    $"{segment.Label} {segment.MsText}",
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, LabelFace, 10, LabelBrush);
                context.DrawText(text, new Point(x + 1, BarHeight + 4));
            }

            x += width + Gap;
        }
    }
}
