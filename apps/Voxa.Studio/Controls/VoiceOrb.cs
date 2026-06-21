using Avalonia;
using Avalonia.Controls.Primitives;

namespace Voxa.Studio.Controls;

/// <summary>
/// The signature Talk voice orb (VST-005 strict-1:1): a layered radial-gradient cyan sphere with a soft
/// outer glow. When <see cref="IsLive"/> two hairline rings ripple outward — the only idle loop on the
/// view besides the logo glow. Template + ripple animations live in <c>Theme/Studio.axaml</c>; the consumer
/// sets <c>Width</c>/<c>Height</c> (the prototype's 132 px) and binds <see cref="IsLive"/> to the session.
/// </summary>
public sealed class VoiceOrb : TemplatedControl
{
    public static readonly StyledProperty<bool> IsLiveProperty =
        AvaloniaProperty.Register<VoiceOrb, bool>(nameof(IsLive));

    public bool IsLive
    {
        get => GetValue(IsLiveProperty);
        set => SetValue(IsLiveProperty, value);
    }
}
