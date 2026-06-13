using CommunityToolkit.Mvvm.ComponentModel;
using Voxa.Studio.Services;

namespace Voxa.Studio.ViewModels;

/// <summary>
/// One editable field on a provider in the Settings dialog (VST-003 WS4). Holds a working-copy value
/// (seeded from the store, flushed back only on Save) and a reveal toggle for secret fields. The
/// reveal toggle never changes <see cref="Value"/> — it only flips the mask.
/// </summary>
public sealed partial class FieldValueViewModel : ObservableObject
{
    public FieldValueViewModel(ProviderFieldDescriptor descriptor, string? initialValue)
    {
        Descriptor = descriptor;
        _value = initialValue ?? "";
    }

    public ProviderFieldDescriptor Descriptor { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectivePasswordChar))]
    private string _value;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectivePasswordChar))]
    private bool _isRevealed;

    /// <summary>A bullet masks a secret field that isn't revealed; '\0' shows the value plainly.</summary>
    public char EffectivePasswordChar => Descriptor.IsSecret && !IsRevealed ? '●' : '\0';
}
