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

    static VoxaMarkControl()
    {
        // Both properties drive the painted output, so a change must invalidate the visual —
        // without this, toggling Glow at runtime (a session going live) changes nothing on screen.
        AffectsRender<VoxaMarkControl>(AnimatedProperty, GlowProperty);
    }

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

    // The brand accent, resolved live from the active theme (falls back to the original cyan).
    private static Color AccentColor =>
        Application.Current?.Resources.TryGetResource("VxAccentBrush", null, out var v) == true
            && v is SolidColorBrush accent ? accent.Color : Color.Parse("#4FC3F7");

    // The V in brand-viewBox units. Total path length ≈ 154; the brand animation uses 160.
    private const double DashUnits = 160.0 / 7.0; // Avalonia dashes are in pen-thickness units
    private static readonly Geometry VPath = Geometry.Parse("M14,18 L50,86 L86,18");

    // Signal bars: x, top, height — width 7, fully rounded (rx 3.5), bottom-anchored spring.
    private static readonly (double X, double Y, double H)[] Bars =
        [(38, 34, 22), (50, 26, 30), (62, 38, 18)];

    private readonly Stopwatch _clock = new();
    private DispatcherTimer? _ticker;
    private bool _introDone;
    private bool _attached;

    // Clock time at which the glow pulse begins. 1.5 s when an intro plays first (the pulse
    // hands off after the draw-on); 0 when glow toggles on without an intro (titlebar going
    // live) so the pulse eases up from this moment rather than snapping mid-cycle.
    private double _glowOriginSeconds = 1.5;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _introDone = !Animated || MotionSettings.ReduceMotion;
        ThemeManager.Changed += OnThemeChanged;   // recolour the mark live on a theme switch

        if (MotionSettings.ReduceMotion) return; // static end-state; AffectsRender repaints Glow changes

        if (!_introDone)
        {
            _glowOriginSeconds = 1.5;   // intro plays from attach; glow follows it
            _clock.Restart();
            EnsureTicker();
        }
        else if (Glow)
        {
            _glowOriginSeconds = 0;     // no intro, but glow is already on — pulse from now
            _clock.Restart();
            EnsureTicker();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        ThemeManager.Changed -= OnThemeChanged;
        StopTicker();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnThemeChanged() => InvalidateVisual();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // Glow is data-backed and toggles at runtime (a Talk session going live/idle). The
        // attach-time ticker decision is stale by then, so re-evaluate the animation here.
        if (change.Property == GlowProperty && _attached)
        {
            SyncGlowTicker();
        }
    }

    private void SyncGlowTicker()
    {
        if (MotionSettings.ReduceMotion) return; // static glow; AffectsRender already repaints
        if (Glow)
        {
            if (_introDone)
            {
                _glowOriginSeconds = 0;   // pulse begins now — there is no intro to hand off from
                _clock.Restart();
            }
            EnsureTicker();
        }
        else if (_introDone)
        {
            StopTicker();   // glow off and no intro running → nothing left to animate
        }
        // glow off mid-intro: leave the ticker running so the draw-on can finish
    }

    private void EnsureTicker()
    {
        if (_ticker is not null) return;
        _ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _ticker.Tick += OnTick;
        _ticker.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_clock.Elapsed.TotalSeconds > 1.7) _introDone = true;
        // Once the intro is done, only a data-backed glow justifies further frames.
        if (_introDone && !Glow) StopTicker();
        InvalidateVisual();
    }

    private void StopTicker()
    {
        if (_ticker is null) return;
        _ticker.Stop();
        _ticker.Tick -= OnTick;
        _ticker = null;
    }

    /// <summary>True while the per-frame ticker is running. Test seam for the glow-on-live path.</summary>
    internal bool IsTickerRunning => _ticker is not null;

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
        if (Glow || (!_introDone && t >= _glowOriginSeconds))
        {
            double strength = MotionSettings.ReduceMotion
                ? 0.5
                : 0.5 - 0.5 * Math.Cos(2 * Math.PI * ((t - _glowOriginSeconds) / Motion.Pulse.TotalSeconds));
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
                new SolidColorBrush(AccentColor, opacity), null,
                new RoundedRect(rect, 3.5));
        }
    }

    private static void DrawV(DrawingContext context, double thickness, double opacity, double dashProgress)
    {
        if (opacity <= 0) return;
        var pen = new Pen(new SolidColorBrush(AccentColor, opacity), thickness)
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
