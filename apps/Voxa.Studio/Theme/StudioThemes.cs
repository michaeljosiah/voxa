using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace Voxa.Studio.Theme;

/// <summary>
/// A selectable palette (VST-003). Each theme overrides the chrome tokens — ink scale, text,
/// hairlines, the single accent. The five semantic stage colours and ok/warn/danger are NOT
/// themed (they encode meaning in charts/traces and must stay constant). Token keys match the
/// resource names in App.axaml without the "Brush" suffix.
/// </summary>
public sealed record StudioTheme(string Id, string Name, IReadOnlyDictionary<string, string> Tokens)
{
    public IBrush SwatchBg => Swatch("VxBg");
    public IBrush SwatchPanel => Swatch("VxPanel2");
    public IBrush SwatchAccent => Swatch("VxAccent");

    private IBrush Swatch(string token) => new SolidColorBrush(Color.Parse(Tokens[token]));
}

/// <summary>The built-in palettes offered in Settings → Appearance.</summary>
public static class StudioThemes
{
    public static readonly StudioTheme Warm = new("warm", "Warm", new Dictionary<string, string>
    {
        ["VxBg"] = "#1C1C19", ["VxPanel"] = "#232320", ["VxPanel2"] = "#2A2A26", ["VxRaised"] = "#33322E",
        ["VxInk700"] = "#3C3B36", ["VxInk600"] = "#47463F", ["VxInk500"] = "#57564D", ["VxInk400"] = "#6E6C61",
        ["VxText"] = "#ECEAE3", ["VxText2"] = "#C0BCB1", ["VxMuted"] = "#8B877B", ["VxOnAccent"] = "#FCFAF6",
        ["VxLine1"] = "#12D8D2C4", ["VxLine2"] = "#20D8D2C4", ["VxLine3"] = "#3CD8D2C4", ["VxBorder"] = "#12D8D2C4",
        ["VxAccent"] = "#C96442", ["VxAccent2"] = "#D97757", ["VxAccentPressed"] = "#AE5536", ["VxAccentDim"] = "#24C96442",
    });

    public static readonly StudioTheme Cool = new("cool", "Cool", new Dictionary<string, string>
    {
        ["VxBg"] = "#0B0F14", ["VxPanel"] = "#11161D", ["VxPanel2"] = "#161C26", ["VxRaised"] = "#1C2330",
        ["VxInk700"] = "#222B36", ["VxInk600"] = "#2C3645", ["VxInk500"] = "#3A4656", ["VxInk400"] = "#4E5C70",
        ["VxText"] = "#EAF1F8", ["VxText2"] = "#A6B2C2", ["VxMuted"] = "#76849B", ["VxOnAccent"] = "#0B0F14",
        ["VxLine1"] = "#1AA6B2C2", ["VxLine2"] = "#29A6B2C2", ["VxLine3"] = "#47A6B2C2", ["VxBorder"] = "#1AA6B2C2",
        ["VxAccent"] = "#4FC3F7", ["VxAccent2"] = "#81D4FA", ["VxAccentPressed"] = "#29A8E0", ["VxAccentDim"] = "#1F4FC3F7",
    });

    public static readonly StudioTheme Slate = new("slate", "Slate", new Dictionary<string, string>
    {
        ["VxBg"] = "#15171A", ["VxPanel"] = "#1B1E22", ["VxPanel2"] = "#20242A", ["VxRaised"] = "#282D34",
        ["VxInk700"] = "#313740", ["VxInk600"] = "#3C434E", ["VxInk500"] = "#4C5563", ["VxInk400"] = "#5F6B7C",
        ["VxText"] = "#E7EAEE", ["VxText2"] = "#B3BAC4", ["VxMuted"] = "#7E8794", ["VxOnAccent"] = "#15171A",
        ["VxLine1"] = "#16AEB8C6", ["VxLine2"] = "#24AEB8C6", ["VxLine3"] = "#40AEB8C6", ["VxBorder"] = "#16AEB8C6",
        ["VxAccent"] = "#7AA2C9", ["VxAccent2"] = "#97B7D6", ["VxAccentPressed"] = "#5E89B4", ["VxAccentDim"] = "#267AA2C9",
    });

    public static readonly IReadOnlyList<StudioTheme> All = [Warm, Cool, Slate];

    public static StudioTheme ById(string? id) =>
        All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Warm;
}

/// <summary>
/// Applies a <see cref="StudioTheme"/> at runtime by mutating the shared <see cref="SolidColorBrush"/>
/// resource objects in <c>Application.Resources</c>. Every view binds the SAME brush instance via
/// StaticResource, so changing a brush's <c>Color</c> repaints the whole app live — no restart, no
/// DynamicResource conversion.
/// </summary>
public static class ThemeManager
{
    /// <summary>Raised after a theme is applied. Custom-drawn controls (the mark) repaint on this.</summary>
    public static event Action? Changed;

    public static void Apply(StudioTheme theme)
    {
        if (Application.Current is not { } app) return;
        foreach (var (token, hex) in theme.Tokens)
            if (app.Resources.TryGetResource($"{token}Brush", ThemeVariant.Dark, out var res) &&
                res is SolidColorBrush brush)
                brush.Color = Color.Parse(hex);

        // Brush mutations repaint bound controls automatically; the code-drawn mark reads the accent
        // at render time, so it needs an explicit nudge to recolour live.
        Changed?.Invoke();
    }
}
