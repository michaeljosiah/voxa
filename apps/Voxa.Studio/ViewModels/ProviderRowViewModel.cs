using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Voxa.Studio.Services;

namespace Voxa.Studio.ViewModels;

/// <summary>
/// One provider identity in the Settings list (VST-003 WS4). Holds the working-copy field editors and
/// a live status that recomputes as the user types — green once every required secret field is filled.
/// Local identities are read-only (no fields, grey "Local" status, no Remove).
/// </summary>
public sealed partial class ProviderRowViewModel : ObservableObject
{
    public ProviderRowViewModel(ProviderManifest manifest, ProviderSecretsService secrets)
    {
        Manifest = manifest;

        var fields = manifest.Fields
            .Select(f => new FieldValueViewModel(f, secrets.GetSecret(manifest.Name, f.Name)))
            .ToList();
        foreach (var field in fields)
            field.PropertyChanged += OnFieldChanged;
        Fields = fields;

        RecomputeStatus();
    }

    public ProviderManifest Manifest { get; }
    public bool IsLocal => Manifest.IsLocal;
    public string DisplayName => Manifest.DisplayName;
    public string Description => Manifest.Description;
    public string RolesLabel => Manifest.RolesLabel;
    public string? DocsUrl => Manifest.DocsUrl;
    public IReadOnlyList<FieldValueViewModel> Fields { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    private ProviderStatus _status;

    public string StatusLabel => Status switch
    {
        ProviderStatus.Local => "Local — no configuration required",
        ProviderStatus.Configured => "Configured",
        ProviderStatus.KeyMissing => "Key missing",
        _ => "",
    };

    private void OnFieldChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FieldValueViewModel.Value)) RecomputeStatus();
    }

    private void RecomputeStatus()
    {
        if (IsLocal) { Status = ProviderStatus.Local; return; }
        var allPresent = Fields
            .Where(f => f.Descriptor.IsSecret)
            .All(f => !string.IsNullOrWhiteSpace(f.Value));
        Status = allPresent ? ProviderStatus.Configured : ProviderStatus.KeyMissing;
    }
}
