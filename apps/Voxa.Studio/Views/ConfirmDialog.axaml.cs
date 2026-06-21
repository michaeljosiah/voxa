using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Voxa.Studio.Views;

/// <summary>
/// A small modal confirm dialog (VST-005 WS6). <see cref="ShowAsync"/> returns true on confirm, false on
/// cancel / Escape / close. Destructive prompts get the danger button + octagon icon; everything else gets
/// the accent primary button + question icon — the reference's confirm vs. danger tones.
/// </summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape) Close(false);
        };
    }

    public static async Task<bool> ShowAsync(Window owner, string title, string body,
        string confirmLabel = "Delete", bool destructive = true)
    {
        var dlg = new ConfirmDialog();
        dlg.TitleText.Text = title;
        dlg.BodyText.Text = body;
        dlg.ConfirmButton.Content = confirmLabel;
        if (!destructive)
        {
            dlg.ConfirmButton.Classes.Set("vx-danger", false);
            dlg.ConfirmButton.Classes.Set("vx-primary", true);
            dlg.IconWrap.Background = (Avalonia.Media.IBrush)dlg.FindResource("VxAccentDimBrush")!;
            dlg.ToneIcon.Foreground = (Avalonia.Media.IBrush)dlg.FindResource("VxAccentBrush")!;
            // a question-mark-in-circle for non-destructive prompts
            dlg.ToneIcon.Data = Avalonia.Media.Geometry.Parse(
                "F0 M12,2 A10,10 0 1 0 12.001,2 Z M9.2,9 A2.8,2.8 0 1 1 12.8,11.6 C12.2,12 12,12.4 12,13.2 L12,14 L10.4,14 L10.4,13 C10.4,11.8 11,11.2 11.8,10.7 A1.2,1.2 0 1 0 10.8,9 Z M11,16 L13,16 L13,18 L11,18 Z");
        }
        return await dlg.ShowDialog<bool>(owner);
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
