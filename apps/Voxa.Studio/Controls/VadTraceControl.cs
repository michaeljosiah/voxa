using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Voxa.Studio.ViewModels;

namespace Voxa.Studio.Controls;

/// <summary>
/// The live VAD strip chart (VST-001 WS2): speech probability per inference window, the
/// configured open threshold as a dashed rule, and shading while the speech gate is open.
/// Hand-drawn — a probability trace needs two primitives, not a charting dependency.
/// The view's render timer swaps <see cref="Samples"/> (an immutable snapshot) ≤30×/s.
/// </summary>
public sealed class VadTraceControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<VadSample>?> SamplesProperty =
        AvaloniaProperty.Register<VadTraceControl, IReadOnlyList<VadSample>?>(nameof(Samples));

    public static readonly StyledProperty<double> ThresholdProperty =
        AvaloniaProperty.Register<VadTraceControl, double>(nameof(Threshold), 0.5);

    static VadTraceControl()
    {
        AffectsRender<VadTraceControl>(SamplesProperty, ThresholdProperty);
    }

    public IReadOnlyList<VadSample>? Samples
    {
        get => GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public double Threshold
    {
        get => GetValue(ThresholdProperty);
        set => SetValue(ThresholdProperty, value);
    }

    private static readonly IBrush GateBrush = new SolidColorBrush(Color.Parse("#4FC3F7"), 0.08);
    private static readonly IPen TracePen = new Pen(new SolidColorBrush(Color.Parse("#4FC3F7")), 1.5);
    private static readonly IPen ThresholdPen = new Pen(
        new SolidColorBrush(Color.Parse("#76849B")), 1, dashStyle: new DashStyle([3, 3], 0));
    private static readonly IPen BaselinePen = new Pen(new SolidColorBrush(Color.Parse("#222B36")), 1);

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        // Quarter gridlines, then the threshold rule.
        for (int q = 1; q < 4; q++)
        {
            var y = h * q / 4.0;
            context.DrawLine(BaselinePen, new Point(0, y), new Point(w, y));
        }
        var ty = h * (1 - Math.Clamp(Threshold, 0, 1));
        context.DrawLine(ThresholdPen, new Point(0, ty), new Point(w, ty));

        var samples = Samples;
        if (samples is null || samples.Count < 2) return;

        // Full window = TraceCapacity samples; until the buffer fills the trace grows from the
        // left, after that it scrolls. One x-step per inference window.
        double step = w / TalkViewModel.TraceCapacity;
        double x0 = 0;

        // Gate-open shading first (under the trace): contiguous runs become translucent bands.
        int runStart = -1;
        for (int i = 0; i <= samples.Count; i++)
        {
            bool open = i < samples.Count && samples[i].GateOpen;
            if (open && runStart < 0) runStart = i;
            if (!open && runStart >= 0)
            {
                context.FillRectangle(GateBrush, new Rect(
                    x0 + runStart * step, 0, (i - runStart) * step, h));
                runStart = -1;
            }
        }

        // The probability polyline.
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(PointAt(0), isFilled: false);
            for (int i = 1; i < samples.Count; i++)
                ctx.LineTo(PointAt(i));
            ctx.EndFigure(false);
        }
        context.DrawGeometry(null, TracePen, geometry);

        Point PointAt(int i) => new(
            x0 + i * step,
            h * (1 - Math.Clamp(samples[i].Probability, 0f, 1f)));
    }
}
