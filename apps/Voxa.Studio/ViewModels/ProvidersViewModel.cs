using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Voxa.Studio.Services;

namespace Voxa.Studio.ViewModels;

/// <summary>
/// The Providers category (VST-003 WS4). A working copy over <see cref="ProviderSecretsService"/>:
/// adding, removing and editing happen only on the view-models here; nothing reaches the service until
/// <see cref="Flush"/> is called from a deliberate Save (so Cancel needs no rollback). Activated cloud
/// identities (<see cref="CloudRows"/>) render inline with their key fields; the four local identities
/// (<see cref="LocalRows"/>) are read-only; the rest are offered in the Add-provider flyout.
/// </summary>
public sealed partial class ProvidersViewModel : ObservableObject
{
    private readonly ProviderSecretsService _secrets;

    public ProvidersViewModel(ProviderSecretsService secrets)
    {
        _secrets = secrets;

        LocalRows = ProviderManifestCatalog.All.Where(m => m.IsLocal)
            .Select(m => new ProviderRowViewModel(m, secrets))
            .ToList();

        CloudRows = new ObservableCollection<ProviderRowViewModel>(
            ProviderManifestCatalog.All.Where(m => !m.IsLocal && secrets.IsActivated(m))
                .Select(m => new ProviderRowViewModel(m, secrets)));

        Available = new ObservableCollection<ProviderManifest>(
            ProviderManifestCatalog.All.Where(m => !m.IsLocal && !secrets.IsActivated(m)));
    }

    /// <summary>Activated cloud identities — editable, with key fields and a Remove.</summary>
    public ObservableCollection<ProviderRowViewModel> CloudRows { get; }

    /// <summary>The four local identities — always present, read-only.</summary>
    public IReadOnlyList<ProviderRowViewModel> LocalRows { get; }

    /// <summary>Cloud identities not yet added — the Add-provider flyout's cards.</summary>
    public ObservableCollection<ProviderManifest> Available { get; }

    /// <summary>All rows (cloud then local) — for tests.</summary>
    public IReadOnlyList<ProviderRowViewModel> Rows => CloudRows.Concat(LocalRows).ToList();

    /// <summary>Add a cloud identity (idempotent). Local names are ignored.</summary>
    public void AddProvider(string manifestName)
    {
        var manifest = Available.FirstOrDefault(m => NameEquals(m.Name, manifestName));
        if (manifest is null) return;

        Available.Remove(manifest);
        CloudRows.Add(new ProviderRowViewModel(manifest, _secrets));
    }

    /// <summary>Remove a cloud identity by name. Local identities are not removable (no-op).</summary>
    public void RemoveProvider(string manifestName)
    {
        var row = CloudRows.FirstOrDefault(r => NameEquals(r.Manifest.Name, manifestName));
        if (row is not null) RemoveProvider(row);
    }

    /// <summary>Remove a cloud identity. Local rows are not removable (no-op).</summary>
    public void RemoveProvider(ProviderRowViewModel row)
    {
        if (row.IsLocal || !CloudRows.Contains(row)) return;

        CloudRows.Remove(row);
        if (!Available.Any(m => NameEquals(m.Name, row.Manifest.Name)))
            Available.Add(row.Manifest);
    }

    [RelayCommand] private void AddManifest(ProviderManifest? manifest) { if (manifest is not null) AddProvider(manifest.Name); }
    [RelayCommand] private void Remove(ProviderRowViewModel? row) { if (row is not null) RemoveProvider(row); }

    /// <summary>
    /// Flush the working copy to the service (called by <see cref="SettingsViewModel.Save"/>):
    /// deactivate cloud identities the user removed (which clears their secrets), then activate and
    /// persist field values for everything still in the list.
    /// </summary>
    internal void Flush()
    {
        var desired = CloudRows
            .Select(r => r.Manifest.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var manifest in ProviderManifestCatalog.All.Where(m => !m.IsLocal))
            if (_secrets.IsActivated(manifest) && !desired.Contains(manifest.Name))
                _secrets.Deactivate(manifest.Name);

        foreach (var row in CloudRows)
        {
            _secrets.Activate(row.Manifest.Name);
            foreach (var field in row.Fields)
                _secrets.SetSecret(row.Manifest.Name, field.Descriptor.Name, field.Value);
        }
    }

    private static bool NameEquals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
