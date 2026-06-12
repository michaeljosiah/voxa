using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using Voxa.Studio.Services;

namespace Voxa.Studio.Views;

/// <summary>Small value→visual converters used by the transcript and the WER diff.</summary>
public static class Converters
{
    /// <summary>User bubbles right-align (cyan-tinted); bot bubbles left-align (panel grey).</summary>
    public static readonly IValueConverter BubbleAlign =
        new FuncValueConverter<bool, HorizontalAlignment>(isUser =>
            isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left);

    public static readonly IValueConverter BubbleBrush =
        new FuncValueConverter<bool, IBrush?>(isUser =>
            isUser ? UserBrush : BotBrush);

    private static readonly IBrush UserBrush = new SolidColorBrush(Color.Parse("#1F4FC3F7")); // accent-soft
    private static readonly IBrush BotBrush = new SolidColorBrush(Color.Parse("#1C2330"));    // ink-800 raised

    // ── WER diff coloring (§6.1): sub = warn, ins = info cyan, del = danger ──

    public static readonly IValueConverter WerForeground =
        new FuncValueConverter<WerOp, IBrush?>(op => op switch
        {
            WerOp.Substitution => WarnBrush,
            WerOp.Insertion => InfoBrush,
            WerOp.Deletion => DangerBrush,
            _ => MatchBrush,
        });

    public static readonly IValueConverter WerBackground =
        new FuncValueConverter<WerOp, IBrush?>(op => op switch
        {
            WerOp.Substitution => WarnSoftBrush,
            WerOp.Insertion => InfoSoftBrush,
            WerOp.Deletion => DangerSoftBrush,
            _ => Brushes.Transparent,
        });

    private static readonly IBrush MatchBrush = new SolidColorBrush(Color.Parse("#A6B2C2"));      // text-2
    private static readonly IBrush WarnBrush = new SolidColorBrush(Color.Parse("#FFB74D"));
    private static readonly IBrush InfoBrush = new SolidColorBrush(Color.Parse("#4FC3F7"));
    private static readonly IBrush DangerBrush = new SolidColorBrush(Color.Parse("#EF5350"));
    private static readonly IBrush WarnSoftBrush = new SolidColorBrush(Color.Parse("#21FFB74D"));
    private static readonly IBrush InfoSoftBrush = new SolidColorBrush(Color.Parse("#1F4FC3F7"));
    private static readonly IBrush DangerSoftBrush = new SolidColorBrush(Color.Parse("#21EF5350"));
}
