using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Voxa.Studio.ViewModels;

namespace Voxa.Studio.Controls;

/// <summary>
/// The builder canvas's slim "last turn" strip (§8.4): one proportional stage-colored bar for
/// the most recent waterfall. Same five colors as the Talk view's full waterfall.
/// </summary>
public sealed class TurnTickerControl : Control
{
    public static readonly StyledProperty<TurnWaterfall?> TurnProperty =
        AvaloniaProperty.Register<TurnTickerControl, TurnWaterfall?>(nameof(Turn));

    public TurnWaterfall? Turn
    {
        get => GetValue(TurnProperty);
        set => SetValue(TurnProperty, value);
    }

    private static readonly Dictionary<string, IBrush> StageBrushes = new()
    {
        ["vad_close"] = new SolidColorBrush(Color.Parse("#76849B")),
        ["stt_final"] = new SolidColorBrush(Color.Parse("#4FC3F7")),
        ["agent_first_token"] = new SolidColorBrush(Color.Parse("#CE93D8")),
        ["tts_first_byte"] = new SolidColorBrush(Color.Parse("#FFB74D")),
        ["audio_out"] = new SolidColorBrush(Color.Parse("#66BB6A")),
    };
    private static readonly IBrush FallbackBrush = new SolidColorBrush(Color.Parse("#4FC3F7"));

    static TurnTickerControl()
    {
        AffectsRender<TurnTickerControl>(TurnProperty);
    }

    public override void Render(DrawingContext context)
    {
        if (Turn is not { } turn || turn.TotalMs <= 0) return;
        var bounds = Bounds;

        using (context.PushClip(new RoundedRect(new Rect(bounds.Size), 3)))
        {
            double x = 0;
            foreach (var segment in turn.Segments)
            {
                var width = bounds.Width * (segment.Ms / turn.TotalMs);
                context.FillRectangle(
                    StageBrushes.GetValueOrDefault(segment.Stage, FallbackBrush),
                    new Rect(x, 0, width, bounds.Height));
                x += width;
            }
        }
    }
}
