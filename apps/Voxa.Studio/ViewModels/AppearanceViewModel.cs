using CommunityToolkit.Mvvm.ComponentModel;
using Voxa.Studio.Services;
using Voxa.Studio.Theme;

namespace Voxa.Studio.ViewModels;

/// <summary>
/// The Settings → Appearance panel (VST-003): pick a theme. Appearance applies <em>live</em> and
/// persists immediately (no Save needed) — selecting a theme repaints the app and writes the choice.
/// </summary>
public sealed partial class AppearanceViewModel : ObservableObject
{
    private readonly StudioPreferences _prefs;

    public AppearanceViewModel(StudioPreferences prefs)
    {
        _prefs = prefs;
        _selectedTheme = StudioThemes.ById(prefs.ThemeId);
    }

    public IReadOnlyList<StudioTheme> Themes => StudioThemes.All;

    [ObservableProperty] private StudioTheme _selectedTheme;

    partial void OnSelectedThemeChanged(StudioTheme value)
    {
        if (value is null) return;
        ThemeManager.Apply(value);
        _prefs.ThemeId = value.Id;
        _prefs.Save();
    }
}
