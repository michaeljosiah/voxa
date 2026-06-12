using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;

namespace Voxa.Studio.Views;

/// <summary>Small bool→visual converters used by the chat transcript.</summary>
public static class Converters
{
    /// <summary>User bubbles right-align (cyan-tinted); bot bubbles left-align (panel grey).</summary>
    public static readonly IValueConverter BubbleAlign =
        new FuncValueConverter<bool, HorizontalAlignment>(isUser =>
            isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left);

    public static readonly IValueConverter BubbleBrush =
        new FuncValueConverter<bool, IBrush?>(isUser =>
            isUser ? UserBrush : BotBrush);

    private static readonly IBrush UserBrush = new SolidColorBrush(Color.Parse("#1B3B4D"));
    private static readonly IBrush BotBrush = new SolidColorBrush(Color.Parse("#161D26"));
}
