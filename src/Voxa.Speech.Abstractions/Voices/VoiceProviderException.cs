namespace Voxa.Speech.Voices;

/// <summary>
/// A voice catalog/clone operation failed in a way the host should show the user — a missing API
/// key, or a provider rejecting the request (plan-gated cloning, quota, an invalid sample). It
/// carries a ready-to-display <see cref="Exception.Message"/> so the UI never surfaces a raw
/// <see cref="HttpRequestException"/>. <see cref="MissingApiKey"/> lets a host render the distinct
/// "key required" affordance rather than a generic error.
/// </summary>
public sealed class VoiceProviderException : Exception
{
    public VoiceProviderException(string message, bool missingApiKey = false, Exception? inner = null)
        : base(message, inner)
        => MissingApiKey = missingApiKey;

    /// <summary>True when the operation failed only because no API key is configured.</summary>
    public bool MissingApiKey { get; }
}
