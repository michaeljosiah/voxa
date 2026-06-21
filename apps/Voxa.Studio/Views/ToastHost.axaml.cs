using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Voxa.Studio.Services;

namespace Voxa.Studio.Views;

/// <summary>
/// Renders the shell's <see cref="ToastService"/> queue bottom-right and owns each toast's auto-dismiss
/// timer (UI-thread), so the service stays Avalonia-free. Toasts auto-close after a few seconds or on
/// the ✕; the slide-in is a style animation in the XAML.
/// </summary>
public partial class ToastHost : UserControl
{
    private static readonly TimeSpan Linger = TimeSpan.FromMilliseconds(3600);
    private readonly Dictionary<ToastItem, DispatcherTimer> _timers = new();
    private ToastService? _service;

    public ToastHost()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Rebind();
        DetachedFromVisualTree += (_, _) => ClearTimers();
    }

    private void Rebind()
    {
        if (_service is not null) _service.Items.CollectionChanged -= OnItemsChanged;
        ClearTimers();
        _service = DataContext as ToastService;
        if (_service is null) return;
        _service.Items.CollectionChanged += OnItemsChanged;
        foreach (var item in _service.Items) StartTimer(item);   // arm any toasts already queued
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (ToastItem item in e.OldItems) StopTimer(item);
        if (e.NewItems is not null)
            foreach (ToastItem item in e.NewItems) StartTimer(item);
        if (e.Action == NotifyCollectionChangedAction.Reset) ClearTimers();
    }

    private void StartTimer(ToastItem item)
    {
        if (_timers.ContainsKey(item)) return;
        var timer = new DispatcherTimer { Interval = Linger };
        timer.Tick += (_, _) => { StopTimer(item); _service?.Dismiss(item); };
        _timers[item] = timer;
        timer.Start();
    }

    private void StopTimer(ToastItem item)
    {
        if (_timers.Remove(item, out var timer)) timer.Stop();
    }

    private void ClearTimers()
    {
        foreach (var timer in _timers.Values) timer.Stop();
        _timers.Clear();
    }

    private void OnToastClose(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ToastItem item }) _service?.Dismiss(item);
    }
}
