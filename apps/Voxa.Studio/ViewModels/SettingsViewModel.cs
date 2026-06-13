using CommunityToolkit.Mvvm.ComponentModel;
using Voxa.Studio.Services;

namespace Voxa.Studio.ViewModels;

/// <summary>A top-level Settings category, shown in the dialog's sidebar (VST-003).</summary>
public enum SettingsCategory { Providers, Appearance }

/// <summary>One sidebar category row: its <see cref="SettingsCategory"/>, label, and nav-icon geometry.</summary>
public sealed record SettingsCategoryItem(SettingsCategory Category, string Name, string IconData);

/// <summary>
/// The Settings dialog root (VST-003 WS4). The sidebar is a list of categories (Providers, Appearance)
/// — exactly one is shown in the content pane at a time, so providers are invisible under Appearance and
/// vice-versa. Save/Cancel govern the provider working copy (Appearance applies live). A fresh instance
/// is created per open, over the shared <see cref="ProviderSecretsService"/>; only <see cref="Save"/>
/// flushes provider changes. <see cref="Saved"/> tells the shell whether to re-apply credentials.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    // 2×2 grid glyph for Providers; half-disc contrast glyph for Appearance.
    private const string ProvidersIcon = "M4,4 H10 V10 H4 Z M14,4 H20 V10 H14 Z M4,14 H10 V20 H4 Z M14,14 H20 V20 H14 Z";
    private const string AppearanceIcon = "M12,3 A9,9 0 1 0 12,21 A9,9 0 0 0 12,3 Z M12,5 A7,7 0 0 1 12,19 Z";

    private readonly ProviderSecretsService _secrets;

    public SettingsViewModel(ProviderSecretsService secrets, StudioPreferences? preferences = null)
    {
        _secrets = secrets;
        Providers = new ProvidersViewModel(secrets);
        Appearance = new AppearanceViewModel(preferences ?? StudioPreferences.Load());
        _selectedCategory = Categories[0];
    }

    public ProvidersViewModel Providers { get; }
    public AppearanceViewModel Appearance { get; }

    public IReadOnlyList<SettingsCategoryItem> Categories { get; } =
    [
        new(SettingsCategory.Providers, "Providers", ProvidersIcon),
        new(SettingsCategory.Appearance, "Appearance", AppearanceIcon),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProviders))]
    [NotifyPropertyChangedFor(nameof(IsAppearance))]
    private SettingsCategoryItem _selectedCategory;

    public bool IsProviders => SelectedCategory?.Category == SettingsCategory.Providers;
    public bool IsAppearance => SelectedCategory?.Category == SettingsCategory.Appearance;

    /// <summary>True once the user saved provider changes — the shell applies new credentials only then.</summary>
    public bool Saved { get; private set; }

    public void Save()
    {
        Providers.Flush();
        _secrets.Save();
        Saved = true;
    }

    /// <summary>
    /// Discard the dialog. The provider working copy lived entirely in the view-models, so the shared
    /// service was never mutated — nothing to roll back. (Appearance changes already persisted live.)
    /// </summary>
    public void Cancel() => Saved = false;
}
