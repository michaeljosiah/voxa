using System.Runtime.InteropServices;
using Avalonia.Animation.Easings;

namespace Voxa.Studio.Theme;

/// <summary>
/// The four motion tokens from VST-002 §3.2 — code-drawn animations (mark, splash, charts)
/// read these so chart and chrome move in one voice. XAML transitions hardcode the same
/// values with a comment; there is no XAML-side TimeSpan token mechanism worth the ceremony.
/// </summary>
public static class Motion
{
    /// <summary>120 ms — hover states, nav highlight, toggles.</summary>
    public static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(120);

    /// <summary>240 ms — view transitions, card entrance, panel slide.</summary>
    public static readonly TimeSpan Standard = TimeSpan.FromMilliseconds(240);

    /// <summary>1100 ms — logo draw-on, first chart paint.</summary>
    public static readonly TimeSpan Draw = TimeSpan.FromMilliseconds(1100);

    /// <summary>2600 ms — the ONLY idle loop (live dot, logo glow), always data-backed.</summary>
    public static readonly TimeSpan Pulse = TimeSpan.FromMilliseconds(2600);

    /// <summary>cubic-bezier(.2,.8,.2,1) — fast/standard interactions.</summary>
    public static readonly Easing EaseOut = new SplineEasing(0.2, 0.8, 0.2, 1.0);

    /// <summary>cubic-bezier(.6,0,.2,1) — the draw-on curve.</summary>
    public static readonly Easing EaseDraw = new SplineEasing(0.6, 0.0, 0.2, 1.0);

    /// <summary>cubic-bezier(.2,.8,.2,1.3) — the signal bars' spring (slight overshoot).</summary>
    public static readonly Easing EaseSpring = new SplineEasing(0.2, 0.8, 0.2, 1.3);
}

/// <summary>
/// Reduced-motion preference (VST-002 §10: every animation behind this check). Windows
/// exposes it via SPI_GETCLIENTAREAANIMATION; elsewhere we default to animations-on.
/// Overridable for tests and as an escape hatch (VOXA_STUDIO_REDUCED_MOTION=1).
/// </summary>
public static class MotionSettings
{
    private static bool? _override;

    public static bool ReduceMotion => _override ?? ReadSystemPreference();

    /// <summary>Test seam / user escape hatch.</summary>
    public static void SetOverride(bool? value) => _override = value;

    private static bool ReadSystemPreference()
    {
        if (Environment.GetEnvironmentVariable("VOXA_STUDIO_REDUCED_MOTION") == "1")
            return true;

        if (OperatingSystem.IsWindows())
        {
            // SPI_GETCLIENTAREAANIMATION: true = animations ENABLED, so reduce when false.
            bool enabled = true;
            if (SystemParametersInfo(0x1042, 0, ref enabled, 0))
                return !enabled;
        }

        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref bool value, uint ignore);
}
