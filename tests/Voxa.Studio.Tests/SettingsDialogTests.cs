using Avalonia.Headless.XUnit;
using Voxa.Studio.ViewModels;
using Voxa.Studio.Views;

namespace Voxa.Studio.Tests;

/// <summary>
/// VST-003 WS5: the Settings dialog's XAML loads and binds on the headless platform — every
/// StaticResource, the status-dot converter, the list/detail templates and the add-flyout resolve,
/// and the detail panel switches between a keyed cloud provider and a fieldless local one.
/// </summary>
public class SettingsDialogTests
{
    [AvaloniaFact]
    public void Settings_Dialog_Loads_And_Binds()
    {
        var services = TestSupport.Services();
        var settings = new SettingsViewModel(services.Secrets);

        var dialog = new SettingsDialog { DataContext = settings };
        dialog.Show();   // resolves every StaticResource + compiled binding + the status converter

        // Regression: a manual InitializeComponent once shadowed the generated one, leaving x:Name
        // fields null — so the add-flyout's card click NRE'd at runtime. The wired field guards it.
        Assert.NotNull(dialog.AddProviderButton);

        // A cloud provider renders its key fields inline; a local one has none.
        settings.Providers.AddProvider("OpenAI");
        Assert.Single(settings.Providers.Rows.Single(r => r.Manifest.Name == "OpenAI").Fields);
        Assert.Empty(settings.Providers.Rows.Single(r => r.Manifest.Name == "Piper").Fields);

        // Categories are mutually exclusive: switching to Appearance hides the providers.
        settings.SelectedCategory = settings.Categories.Single(c => c.Category == SettingsCategory.Appearance);
        Assert.True(settings.IsAppearance);
        Assert.False(settings.IsProviders);

        dialog.Close();
    }
}
