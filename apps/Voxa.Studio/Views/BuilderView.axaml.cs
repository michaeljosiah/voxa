using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Voxa.Studio.Theme;
using Voxa.Studio.ViewModels;

namespace Voxa.Studio.Views;

/// <summary>
/// Canvas interactions for the Builder (drag nodes, drag wires from ports, zoom, shortcuts) and
/// the ≤30 fps drain timer. All decisions live in <see cref="BuilderViewModel"/> — this file is
/// pointer plumbing only.
/// </summary>
public partial class BuilderView : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };

    private BuilderNodeVm? _dragNode;
    private Point _dragOffset;
    private bool _dragMoved;
    private BuilderNodeVm? _wireFrom;
    private double _zoom = 1.0;

    private BuilderViewModel? Vm => DataContext as BuilderViewModel;

    // Payload key for a palette → canvas drag (adding a node is drag-only; clicking does nothing).
    private const string PaletteFormat = "voxa/palette-entry";

    public BuilderView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) =>
        {
            if (Vm is not { } vm) return;
            vm.DrainPending();
            if (vm.IsRunning) EdgesLayer.InvalidateVisual(); // particles move; idle canvas stays still
        };

        AddHandler(DragDrop.DragOverEvent, OnPaletteDragOver);
        AddHandler(DragDrop.DropEvent, OnPaletteDrop);
    }

    // ── add a node: drag from the palette onto the canvas (the only add path) ──

    private async void OnPalettePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: BuilderPaletteEntry entry }) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var data = new DataObject();
        data.Set(PaletteFormat, entry);
        await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy);
    }

    private void OnPaletteDragOver(object? sender, DragEventArgs e)
        => e.DragEffects = e.Data.Contains(PaletteFormat) ? DragDropEffects.Copy : DragDropEffects.None;

    private void OnPaletteDrop(object? sender, DragEventArgs e)
    {
        if (Vm is not { } vm || e.Data.Get(PaletteFormat) is not BuilderPaletteEntry entry) return;
        var pos = e.GetPosition(GraphPanel);
        vm.AddNodeAt(entry, pos.X, pos.Y);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    // ── node dragging ────────────────────────────────────────────────────────

    private void OnNodePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { DataContext: BuilderNodeVm node }) return;
        Vm?.Select(node);
        Focus();
        // Mark handled even for a non-left press so it never bubbles to OnCanvasPressed, which
        // would clear the selection we just made (a right/middle click would deselect the node).
        e.Handled = true;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragNode = node;
        _dragMoved = false;
        var pos = e.GetPosition(GraphPanel);
        _dragOffset = new Point(pos.X - node.X, pos.Y - node.Y);
    }

    private void OnNodeMoved(object? sender, PointerEventArgs e)
    {
        if (_dragNode is not { } node) return;
        if (!_dragMoved)
        {
            _dragMoved = true;
            Vm?.PushUndo(); // one undo step per drag, not per pixel
        }
        var pos = e.GetPosition(GraphPanel);
        node.X = Math.Max(0, pos.X - _dragOffset.X);
        node.Y = Math.Max(0, pos.Y - _dragOffset.Y);
    }

    private void OnNodeReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragNode is { } node && _dragMoved)
            Vm?.SnapNode(node);
        _dragNode = null;
    }

    // ── wire dragging (out port → any spot on a target node) ────────────────

    private void OnPortPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Ellipse { DataContext: BuilderNodeVm node }) return;
        _wireFrom = node;
        var start = new Point(node.X + BuilderNodeVm.NodeWidth, node.Y + BuilderNodeVm.NodeHeight / 2);
        EdgesLayer.TempWireStart = start;
        EdgesLayer.TempWireEnd = start;
        e.Pointer.Capture(GraphPanel); // moves/releases land on the canvas, not the tiny port
        e.Handled = true;
    }

    private void OnCanvasMoved(object? sender, PointerEventArgs e)
    {
        if (_wireFrom is null) return;
        EdgesLayer.TempWireEnd = e.GetPosition(GraphPanel);
    }

    private void OnCanvasReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_wireFrom is not { } from) return;
        _wireFrom = null;
        EdgesLayer.TempWireStart = null;
        EdgesLayer.TempWireEnd = null;
        if (Vm is not { } vm) return;

        var pos = e.GetPosition(GraphPanel);
        var target = vm.Nodes.FirstOrDefault(n =>
            pos.X >= n.X - 10 && pos.X <= n.X + BuilderNodeVm.NodeWidth + 10 &&
            pos.Y >= n.Y - 6 && pos.Y <= n.Y + BuilderNodeVm.NodeHeight + 6);
        if (target is null || target == from) return;

        if (!vm.TryConnect(from, target, out var reason) && reason is not null)
        {
            vm.StatusText = reason; // the one-line why; the wire itself snapped back already
            ShakeNode(target);
        }
    }

    private static void ShakeNode(BuilderNodeVm node)
    {
        // Reduced motion: skip the shake (the one-line reason in the status strip still carries
        // the feedback) — VST-002 §10, "every animation behind this check".
        if (MotionSettings.ReduceMotion) return;
        node.IsRefused = true;
        DispatcherTimer.RunOnce(() => node.IsRefused = false, TimeSpan.FromMilliseconds(280));
    }

    private void OnCanvasPressed(object? sender, PointerPressedEventArgs e)
    {
        // Clicking bare canvas (not a node — those mark the event handled) clears selection.
        Vm?.Select(null);
        Focus();
    }

    // ── zoom (Ctrl+wheel, 0.6–1.6) ───────────────────────────────────────────

    private void OnCanvasWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        _zoom = Math.Clamp(_zoom * (e.Delta.Y > 0 ? 1.1 : 1 / 1.1), 0.6, 1.6);
        ZoomHost.LayoutTransform = new ScaleTransform(_zoom, _zoom);
        e.Handled = true;
    }

    // ── '+' affordance: a flyout of only type-compatible nodes ──────────────

    private void OnPlusClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: BuilderNodeVm node } button || Vm is not { } vm) return;
        vm.Select(node); // PlusChoices follow the selected node's dangling port

        var flyout = new MenuFlyout();
        foreach (var choice in vm.PlusChoices)
        {
            flyout.Items.Add(new MenuItem
            {
                Header = choice.Label,
                Command = vm.AddAndConnectCommand,
                CommandParameter = choice,
            });
        }
        flyout.ShowAt(button);
    }

    // ── shortcuts + clipboard ────────────────────────────────────────────────

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (e.Key == Key.Delete && vm.SelectedNode is not null)
        {
            vm.RemoveSelectedCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            vm.Undo();
            e.Handled = true;
        }
        else if (e.Key == Key.Y && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            vm.Redo();
            e.Handled = true;
        }
    }

    private async void OnCopyExport(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Vm is { ExportText.Length: > 0 } vm && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(vm.ExportText);
    }
}
