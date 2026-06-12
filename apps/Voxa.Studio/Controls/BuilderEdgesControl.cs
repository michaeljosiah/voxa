using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Voxa.Studio.Services;
using Voxa.Studio.ViewModels;

namespace Voxa.Studio.Controls;

/// <summary>
/// The builder canvas underlay: the 40 px grid, the Bézier wires with arrowheads, the drag-wire
/// preview, and — while live — frame-flow particles. Particles are real signals only (§8.4):
/// a pulse per final transcript / agent delta / TTS chunk, shimmer while the VAD gate is open.
/// </summary>
public sealed class BuilderEdgesControl : Control
{
    public static readonly StyledProperty<IEnumerable<BuilderNodeVm>?> NodesProperty =
        AvaloniaProperty.Register<BuilderEdgesControl, IEnumerable<BuilderNodeVm>?>(nameof(Nodes));

    public static readonly StyledProperty<IEnumerable<BuilderEdgeVm>?> EdgesProperty =
        AvaloniaProperty.Register<BuilderEdgesControl, IEnumerable<BuilderEdgeVm>?>(nameof(Edges));

    public static readonly StyledProperty<Point?> TempWireStartProperty =
        AvaloniaProperty.Register<BuilderEdgesControl, Point?>(nameof(TempWireStart));

    public static readonly StyledProperty<Point?> TempWireEndProperty =
        AvaloniaProperty.Register<BuilderEdgesControl, Point?>(nameof(TempWireEnd));

    public static readonly StyledProperty<bool> IsLiveProperty =
        AvaloniaProperty.Register<BuilderEdgesControl, bool>(nameof(IsLive));

    public IEnumerable<BuilderNodeVm>? Nodes
    {
        get => GetValue(NodesProperty);
        set => SetValue(NodesProperty, value);
    }

    public IEnumerable<BuilderEdgeVm>? Edges
    {
        get => GetValue(EdgesProperty);
        set => SetValue(EdgesProperty, value);
    }

    public Point? TempWireStart
    {
        get => GetValue(TempWireStartProperty);
        set => SetValue(TempWireStartProperty, value);
    }

    public Point? TempWireEnd
    {
        get => GetValue(TempWireEndProperty);
        set => SetValue(TempWireEndProperty, value);
    }

    public bool IsLive
    {
        get => GetValue(IsLiveProperty);
        set => SetValue(IsLiveProperty, value);
    }

    private const double Grid = 40;
    private const double PulseMs = 900;

    private static readonly IBrush GridBrush = new SolidColorBrush(Color.Parse("#10FFFFFF"));
    private static readonly IBrush WireBrush = new SolidColorBrush(Color.Parse("#4FC3F7"));
    private static readonly Pen GridPen = new(GridBrush, 1);
    private static readonly Pen WirePen = new(WireBrush, 2) { LineCap = PenLineCap.Round };
    private static readonly Pen LiveWirePen = new(new SolidColorBrush(Color.Parse("#4FC3F7"), 0.9), 2);
    private static readonly Pen TempPen = new(WireBrush, 2) { DashStyle = new DashStyle([3, 3], 0) };

    // Port/particle colors are the stage palette — frame types mean the same stages everywhere.
    internal static readonly Dictionary<BuilderPortType, IBrush> PortBrushes = new()
    {
        [BuilderPortType.Audio] = new SolidColorBrush(Color.Parse("#76849B")),
        [BuilderPortType.Transcription] = new SolidColorBrush(Color.Parse("#4FC3F7")),
        [BuilderPortType.AgentText] = new SolidColorBrush(Color.Parse("#CE93D8")),
        [BuilderPortType.SynthAudio] = new SolidColorBrush(Color.Parse("#FFB74D")),
    };

    static BuilderEdgesControl()
    {
        AffectsRender<BuilderEdgesControl>(
            NodesProperty, EdgesProperty, TempWireStartProperty, TempWireEndProperty, IsLiveProperty);
    }

    // Re-render when collections mutate or nodes move (drag). Live particles are driven by
    // the view's session timer instead — no idle animation loop.
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == NodesProperty)
        {
            Hook(change.NewValue as INotifyCollectionChanged);
            if (change.NewValue is IEnumerable<BuilderNodeVm> nodes)
                foreach (var node in nodes) HookNode(node);
        }
        else if (change.Property == EdgesProperty)
        {
            Hook(change.NewValue as INotifyCollectionChanged);
        }
    }

    private void Hook(INotifyCollectionChanged? collection)
    {
        if (collection is null) return;
        collection.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null)
                foreach (var item in e.NewItems.OfType<BuilderNodeVm>()) HookNode(item);
            InvalidateVisual();
        };
    }

    private void HookNode(BuilderNodeVm node) => node.PropertyChanged += OnNodeChanged;

    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BuilderNodeVm.X) or nameof(BuilderNodeVm.Y))
            InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        for (double x = 0; x <= bounds.Width; x += Grid)
            context.DrawLine(GridPen, new Point(x, 0), new Point(x, bounds.Height));
        for (double y = 0; y <= bounds.Height; y += Grid)
            context.DrawLine(GridPen, new Point(0, y), new Point(bounds.Width, y));

        if (Edges is null) return;
        var now = Environment.TickCount64;

        foreach (var edge in Edges)
        {
            var p1 = new Point(edge.From.X + BuilderNodeVm.NodeWidth, edge.From.Y + BuilderNodeVm.NodeHeight / 2);
            var p2 = new Point(edge.To.X, edge.To.Y + BuilderNodeVm.NodeHeight / 2);
            var mx = (p1.X + p2.X) / 2;
            var c1 = new Point(mx, p1.Y);
            var c2 = new Point(mx, p2.Y);

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(p1, false);
                ctx.CubicBezierTo(c1, c2, new Point(p2.X - 7, p2.Y));
                ctx.EndFigure(false);
            }
            context.DrawGeometry(null, IsLive ? LiveWirePen : WirePen, geometry);

            // Arrowhead: the curve arrives horizontally (both control points share the end Y).
            var arrow = new StreamGeometry();
            using (var ctx = arrow.Open())
            {
                ctx.BeginFigure(new Point(p2.X - 8, p2.Y - 4.5), true);
                ctx.LineTo(new Point(p2.X, p2.Y));
                ctx.LineTo(new Point(p2.X - 8, p2.Y + 4.5));
                ctx.EndFigure(true);
            }
            context.DrawGeometry(WireBrush, null, arrow);

            if (!IsLive) continue;
            var brush = PortBrushes[edge.PortType];

            if (edge.IsFlowing)
            {
                // Gate-open shimmer: three dots cycling the wire (~1.1 s per traversal).
                for (var k = 0; k < 3; k++)
                {
                    var t = (now / 1100.0 + k / 3.0) % 1.0;
                    context.DrawEllipse(brush, null, Bezier(p1, c1, c2, p2, t), 2.6, 2.6);
                }
            }
            else if (now - edge.LastPulseTick < PulseMs)
            {
                // One bright pulse per real frame event.
                var t = (now - edge.LastPulseTick) / PulseMs;
                context.DrawEllipse(brush, null, Bezier(p1, c1, c2, p2, t), 3.2, 3.2);
            }
        }

        if (TempWireStart is { } start && TempWireEnd is { } end)
            context.DrawLine(TempPen, start, end);
    }

    private static Point Bezier(Point p0, Point p1, Point p2, Point p3, double t)
    {
        var u = 1 - t;
        var x = u * u * u * p0.X + 3 * u * u * t * p1.X + 3 * u * t * t * p2.X + t * t * t * p3.X;
        var y = u * u * u * p0.Y + 3 * u * u * t * p1.Y + 3 * u * t * t * p2.Y + t * t * t * p3.Y;
        return new Point(x, y);
    }
}
