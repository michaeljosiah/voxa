using System.Collections.ObjectModel;

namespace Voxa.Studio.Services;

/// <summary>Toast severity → icon + accent (mirrors the reference's tone set / the badge tones).</summary>
public enum ToastTone { Info, Success, Warning, Danger }

/// <summary>A single transient toast. Immutable; the host owns its dismissal timer.</summary>
public sealed class ToastItem
{
    public required ToastTone Tone { get; init; }
    public required string Title { get; init; }
    public string? Message { get; init; }

    /// <summary>Render the message in the mono face (durations, ids) — like the reference's <c>mono</c> flag.</summary>
    public bool Mono { get; init; }
}

/// <summary>
/// The shell's toast queue (VST-005 WS6). Avalonia-free — it only owns the observable list; the
/// <c>ToastHost</c> view renders cards and runs the per-toast auto-dismiss timer on the UI thread.
/// Mutated only from UI-thread actions (session edges, profile switch, settings save, Config apply).
/// </summary>
public sealed class ToastService
{
    public ObservableCollection<ToastItem> Items { get; } = new();

    public ToastItem Show(ToastTone tone, string title, string? message = null, bool mono = false)
    {
        var item = new ToastItem { Tone = tone, Title = title, Message = message, Mono = mono };
        Items.Add(item);
        // A burst of toasts shouldn't tower up the screen — keep the most recent few.
        while (Items.Count > 4) Items.RemoveAt(0);
        return item;
    }

    public void Dismiss(ToastItem item) => Items.Remove(item);
}
