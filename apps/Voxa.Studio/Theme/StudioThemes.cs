using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace Voxa.Studio.Theme;

/// <summary>
/// A selectable palette (VST-003; VST-005 = accent-only). Every theme shares the cool Ink base —
/// only the single accent ramp (VxAccent/VxAccent2/VxAccentPressed/VxAccentDim) differs, so the
/// chrome stays calm and constant and just the accent re-tints. The five semantic stage colours and
/// ok/warn/danger are NOT themed (they encode meaning in charts/traces and must stay constant).
/// Token keys match the resource names in App.axaml without the "Brush" suffix.
/// </summary>
public sealed record StudioTheme(string Id, string Name, IReadOnlyDictionary<string, string> Tokens)
{
    public IBrush SwatchBg => Swatch("VxBg");
    public IBrush SwatchPanel => Swatch("VxPanel2");
    public IBrush SwatchAccent => Swatch("VxAccent");

    private IBrush Swatch(string token) => new SolidColorBrush(Color.Parse(Tokens[token]));
}

/// <summary>
/// The built-in palettes offered in Settings → Appearance. <b>Cool</b> (Voxa Pulse cyan) is the
/// default; Warm and Slate re-tint only the accent ramp over the same cool base.
/// </summary>
public static class StudioThemes
{
    // The shared cool Ink base (matches App.axaml). Identical across every theme, so applying a
    // theme only ever re-tints the accent — the surfaces, text, and hairlines never move.
    private static readonly Dictionary<string, string> CoolBase = new()
    {
        ["VxBg"] = "#0B0F14", ["VxPanel"] = "#11161D", ["VxPanel2"] = "#161C26", ["VxRaised"] = "#1C2330",
        ["VxInk700"] = "#222B36", ["VxInk600"] = "#2C3645", ["VxInk500"] = "#3A4656", ["VxInk400"] = "#4E5C70",
        ["VxText"] = "#EAF1F8", ["VxText2"] = "#A6B2C2", ["VxMuted"] = "#76849B", ["VxOnAccent"] = "#0B0F14",
        ["VxLine1"] = "#1AA6B2C2", ["VxLine2"] = "#29A6B2C2", ["VxLine3"] = "#47A6B2C2", ["VxBorder"] = "#1AA6B2C2",
    };

    private static StudioTheme Accent(string id, string name, string a, string a2, string pressed, string dim) =>
        new(id, name, new Dictionary<string, string>(CoolBase)
        {
            ["VxAccent"] = a, ["VxAccent2"] = a2, ["VxAccentPressed"] = pressed, ["VxAccentDim"] = dim,
        });

    public static readonly StudioTheme Cool  = Accent("cool",  "Cool",  "#4FC3F7", "#81D4FA", "#29A8E0", "#1F4FC3F7");
    public static readonly StudioTheme Warm  = Accent("warm",  "Warm",  "#E27A4E", "#F0A87E", "#C25E36", "#21E27A4E");
    public static readonly StudioTheme Slate = Accent("slate", "Slate", "#8AA2CE", "#AEC0E2", "#6B83B3", "#218AA2CE");

    // Display order in the Appearance picker (matches the reference prototype): Warm · Cool · Slate.
    public static readonly IReadOnlyList<StudioTheme> All = [Warm, Cool, Slate];

    public static StudioTheme ById(string? id) =>
        All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Cool;
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
