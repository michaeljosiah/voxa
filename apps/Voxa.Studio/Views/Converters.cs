using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using Voxa.Studio.Controls;
using Voxa.Studio.Services;
using Voxa.Studio.ViewModels;

namespace Voxa.Studio.Views;

/// <summary>Small value→visual converters used by the transcript and the WER diff.</summary>
public static class Converters
{
    /// <summary>User bubbles right-align (cyan-tinted); bot bubbles left-align (panel grey).</summary>
    public static readonly IValueConverter BubbleAlign =
        new FuncValueConverter<bool, HorizontalAlignment>(isUser =>
            isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left);

    // User bubbles are accent-soft, bot bubbles are the raised surface — both resolved from the live
    // theme so they follow a theme switch (the accent-soft tint becomes coral under the Warm theme).
    public static readonly IValueConverter BubbleBrush =
        new FuncValueConverter<bool, IBrush?>(isUser =>
            Themed(isUser ? "VxAccentDimBrush" : "VxRaisedBrush"));

    private static IBrush? Themed(string key) =>
        Avalonia.Application.Current?.Resources.TryGetResource(key, null, out var v) == true ? v as IBrush : null;

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

    // ── builder canvas (§8): stage accents on node cards, frame-type port dots ──

    public static readonly IValueConverter StageBrush =
        new FuncValueConverter<string?, IBrush?>(key => key switch
        {
            "vad" => StageVadBrush,
            "stt" => StageSttBrush,
            "agent" => StageAgentBrush,
            "tts" => StageTtsBrush,
            "out" => StageOutBrush,
            _ => StageVadBrush,
        });

    /// <summary>Settings status dot (VST-003): configured green, key-missing amber, local grey.</summary>
    public static readonly IValueConverter ProviderStatusBrush =
        new FuncValueConverter<ProviderStatus, IBrush?>(status => status switch
        {
            ProviderStatus.Configured => StageOutBrush, // good green
            ProviderStatus.KeyMissing => WarnBrush,      // amber
            _ => StageVadBrush,                          // local grey
        });

    public static readonly IValueConverter PortBrush =
        new FuncValueConverter<BuilderPortType?, IBrush?>(type =>
            type is { } t ? BuilderEdgesControl.PortBrushes[t] : Brushes.Transparent);

    public static readonly IValueConverter PortLabel =
        new FuncValueConverter<BuilderPortType?, string>(type =>
            type is { } t ? BuilderGraph.PortLabel(t) : "—");

    /// <summary>(IsSelected, StageKey, HasError) → border ring: red when invalid, stage accent when
    /// selected, hairline otherwise. The error state wins so a bad node is unmistakable.</summary>
    public static readonly IMultiValueConverter NodeRing =
        new FuncMultiValueConverter<object?, IBrush?>(values =>
        {
            var items = values.ToList();
            if (items is [bool selected, string key, bool hasError])
                return hasError ? (Themed("VxBadBrush") ?? BadBrush)
                     : selected ? (IBrush?)StageBrush.Convert(key, typeof(IBrush), null, CultureInfo.InvariantCulture)
                     : Line2Brush;
            return items is [bool s, string k]
                ? s ? (IBrush?)StageBrush.Convert(k, typeof(IBrush), null, CultureInfo.InvariantCulture) : Line2Brush
                : Line2Brush;
        });

    /// <summary>(IsActive, StageKey) → the live stage glow (§8.4); none while idle.</summary>
    public static readonly IMultiValueConverter NodeGlow =
        new FuncMultiValueConverter<object?, BoxShadows>(values =>
        {
            var items = values.ToList();
            if (items is not [true, string key]) return default;
            var brush = (ISolidColorBrush?)StageBrush.Convert(key, typeof(IBrush), null, CultureInfo.InvariantCulture);
            var color = brush?.Color ?? Colors.Transparent;
            return new BoxShadows(new BoxShadow
            {
                Blur = 22, Spread = -4, Color = Color.FromArgb(0x66, color.R, color.G, color.B),
            });
        });

    /// <summary>TalkPhase → status-pill accent: warm-up amber, listening green, hearing accent,
    /// transcribing cyan, thinking purple, speaking orange, idle grey.</summary>
    public static readonly IValueConverter PhaseBrush =
        new FuncValueConverter<TalkPhase, IBrush?>(phase => phase switch
        {
            TalkPhase.WarmingUp    => WarnBrush,
            TalkPhase.Listening    => StageOutBrush,
            TalkPhase.Hearing      => Themed("VxAccentBrush") ?? StageSttBrush,
            TalkPhase.Transcribing => StageSttBrush,
            TalkPhase.Thinking     => StageAgentBrush,
            TalkPhase.Speaking     => StageTtsBrush,
            _                      => MatchBrush,
        });

    // ── toasts (VST-005 WS6): tone → accent + icon + message face ──

    public static readonly IValueConverter ToastAccent =
        new FuncValueConverter<ToastTone, IBrush?>(tone => tone switch
        {
            ToastTone.Success => StageOutBrush,
            ToastTone.Warning => WarnBrush,
            ToastTone.Danger => DangerBrush,
            _ => InfoBrush,
        });

    /// <summary>Tone → a filled even-odd glyph (check / triangle / octagon / info dot).</summary>
    public static readonly IValueConverter ToastIcon =
        new FuncValueConverter<ToastTone, Geometry?>(tone => Geometry.Parse(tone switch
        {
            ToastTone.Success => "F1 M4,12 L9,17 L20,5 L18,3 L9,13 L6,10 Z",
            ToastTone.Warning => "F0 M12,3 L22,20 L2,20 Z M11,9 L13,9 L13,14 L11,14 Z M11,16 L13,16 L13,18 L11,18 Z",
            ToastTone.Danger => "F0 M8,2 L16,2 L22,8 L22,16 L16,22 L8,22 L2,16 L2,8 Z M11,7 L13,7 L13,13 L11,13 Z M11,15 L13,15 L13,17 L11,17 Z",
            _ => "F0 M12,2 A10,10 0 1 0 12.001,2 Z M11,6 L13,6 L13,8 L11,8 Z M11,10 L13,10 L13,17 L11,17 Z",
        }));

    public static readonly IValueConverter ToastMessageFont =
        new FuncValueConverter<bool, FontFamily?>(mono =>
            Avalonia.Application.Current?.Resources[mono ? "VxMono" : "VxUi"] as FontFamily);

    private static readonly IBrush Line2Brush = new SolidColorBrush(Color.Parse("#29A6B2C2")); // line-2
    private static readonly IBrush BadBrush = new SolidColorBrush(Color.Parse("#EF5350"));     // invalid-node ring fallback

    private static readonly IBrush StageVadBrush = new SolidColorBrush(Color.Parse("#76849B"));
    private static readonly IBrush StageSttBrush = new SolidColorBrush(Color.Parse("#4FC3F7"));
    private static readonly IBrush StageAgentBrush = new SolidColorBrush(Color.Parse("#CE93D8"));
    private static readonly IBrush StageTtsBrush = new SolidColorBrush(Color.Parse("#FFB74D"));
    private static readonly IBrush StageOutBrush = new SolidColorBrush(Color.Parse("#66BB6A"));

    private static readonly IBrush MatchBrush = new SolidColorBrush(Color.Parse("#A6B2C2"));      // text-2
    private static readonly IBrush WarnBrush = new SolidColorBrush(Color.Parse("#FFB74D"));
    private static readonly IBrush InfoBrush = new SolidColorBrush(Color.Parse("#4FC3F7"));
    private static readonly IBrush DangerBrush = new SolidColorBrush(Color.Parse("#EF5350"));
    private static readonly IBrush WarnSoftBrush = new SolidColorBrush(Color.Parse("#21FFB74D"));
    private static readonly IBrush InfoSoftBrush = new SolidColorBrush(Color.Parse("#1F4FC3F7"));
    private static readonly IBrush DangerSoftBrush = new SolidColorBrush(Color.Parse("#21EF5350"));
}
