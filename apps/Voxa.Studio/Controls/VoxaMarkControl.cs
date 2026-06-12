using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Voxa.Studio.Theme;

namespace Voxa.Studio.Controls;

/// <summary>
/// The VOXA mark (VST-002 §3.1): a V cut by a waveform — two clean strokes forming the V,
/// three signal bars rising inside the cut. Geometry is the brand SVG's 100×100 viewBox
/// (V: 14,18 → 50,86 → 86,18 at stroke 7; bars on a 22/30/18 rhythm), scaled to the control.
///
/// <para>
/// <see cref="Animated"/> plays the §4 draw-on once on attach: the V strokes sweep in over
/// 1100 ms (the draw token), the bars spring up at 0.9/1.0/1.1 s with slight overshoot.
/// <see cref="Glow"/> runs the 2600 ms glow pulse — an idle loop, so callers may only set it
/// when it is data-backed (a live session, the splash's "initializing" state). Reduced motion
/// renders the finished mark immediately, with a constant soft glow instead of a pulse.
/// </para>
/// </summary>
public sealed class VoxaMarkControl : Control
{
    public static readonly StyledProperty<bool> AnimatedProperty =
        AvaloniaProperty.Register<VoxaMarkControl, bool>(nameof(Animated));

    public static readonly StyledProperty<bool> GlowProperty =
        AvaloniaProperty.Register<VoxaMarkControl, bool>(nameof(Glow));

    /// <summary>Play the draw-on intro when the control attaches.</summary>
    public bool Animated
    {
        get => GetValue(AnimatedProperty);
        set => SetValue(AnimatedProperty, value);
    }

    /// <summary>Run the glow pulse. Only set when data-backed (live session / init in progress).</summary>
    public bool Glow
    {
        get => GetValue(GlowProperty);
        set => SetValue(GlowProperty, value);
    }

    private static readonly Color Pulse400 = Color.Parse("#4FC3F7");

    // The V in brand-viewBox units. Total path length ≈ 154; the brand animation uses 160.
    private const double DashUnits = 160.0 / 7.0; // Avalonia dashes are in pen-thickness units
    private static readonly Geometry VPath = Geometry.Parse("M14,18 L50,86 L86,18");

    // Signal bars: x, top, height — width 7, fully rounded (rx 3.5), bottom-anchored spring.
    private static readonly (double X, double Y, double H)[] Bars =
        [(38, 34, 22), (50, 26, 30), (62, 38, 18)];

    private readonly Stopwatch _clock = new();
    private DispatcherTimer? _ticker;
    private bool _introDone;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _introDone = !Animated || MotionSettings.ReduceMotion;
        var needsTicker = (!_introDone || Glow) && !MotionSettings.ReduceMotion;
        if (needsTicker)
        {
            _clock.Restart();
            _ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _ticker.Tick += (_, _) =>
            {
                if (_clock.Elapsed.TotalSeconds > 1.7) _introDone = true;
                // Once the intro is done, only a data-backed glow justifies further frames.
                if (_introDone && !Glow) StopTicker();
                InvalidateVisual();
            };
            _ticker.Start();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        StopTicker();
        base.OnDetachedFromVisualTree(e);
    }

    private void StopTicker()
    {
        _ticker?.Stop();
        _ticker = null;
    }

    /// <summary>Jump the intro to its end state — the splash's click-to-skip.</summary>
    public void CompleteIntro()
    {
        _introDone = true;
        if (!Glow) StopTicker();
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0) return;
        var scale = size / 100.0;
        using var _ = context.PushTransform(
            Matrix.CreateScale(scale, scale) *
            Matrix.CreateTranslation((Bounds.Width - size) / 2, (Bounds.Height - size) / 2));

        var t = _clock.Elapsed.TotalSeconds;

        // ── glow under everything: pulsing when animated, constant-soft when static ──
        if (Glow || (!_introDone && t >= 1.5))
        {
            double strength = MotionSettings.ReduceMotion
                ? 0.5
                : 0.5 - 0.5 * Math.Cos(2 * Math.PI * ((t - 1.5) / Motion.Pulse.TotalSeconds));
            if (!Glow && _introDone) strength = 0;
            // No primitive blur in DrawingContext — layered wide translucent strokes read as glow.
            DrawV(context, thickness: 20, opacity: 0.05 * strength, dashProgress: 1);
            DrawV(context, thickness: 13, opacity: 0.14 * strength, dashProgress: 1);
        }

        // ── the V: draw-on via dash offset (1100 ms, ease-draw) ──
        double vProgress = _introDone ? 1 : Motion.EaseDraw.Ease(Math.Clamp(t / Motion.Draw.TotalSeconds, 0, 1));
        DrawV(context, thickness: 7, opacity: 1, dashProgress: vProgress);

        // ── the bars: bottom-anchored spring at 0.9/1.0/1.1 s ──
        for (int i = 0; i < Bars.Length; i++)
        {
            var (x, y, h) = Bars[i];
            double p = _introDone ? 1 : Math.Clamp((t - (0.9 + 0.1 * i)) / 0.5, 0, 1);
            if (p <= 0) continue;
            double grow = 0.2 + 0.8 * Motion.EaseSpring.Ease(p); // may overshoot past 1 — that's the spring
            double opacity = Math.Min(p * 3, 1);
            double height = h * grow;
            var rect = new Rect(x, y + h - height, 7, height);
            context.DrawRectangle(
                new SolidColorBrush(Pulse400, opacity), null,
                new RoundedRect(rect, 3.5));
        }
    }

    private static void DrawV(DrawingContext context, double thickness, double opacity, double dashProgress)
    {
        if (opacity <= 0) return;
        var pen = new Pen(new SolidColorBrush(Pulse400, opacity), thickness)
        {
            LineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
            DashStyle = dashProgress >= 1
                ? null
                : new DashStyle([DashUnits, DashUnits], DashUnits * (1 - dashProgress) * 7 / thickness),
        };
        context.DrawGeometry(null, pen, VPath);
    }
}
