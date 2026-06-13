namespace Voxa.Speech.Voices;

/// <summary>
/// A single voice as Voxa sees it, regardless of where it lives. The <see cref="Id"/> is the
/// provider-native handle the TTS engine needs at synthesis time (an ElevenLabs <c>voice_id</c>,
/// a Mistral voice name, a local embedding id), so a picker can write it straight into config.
/// </summary>
/// <param name="Id">Provider-native id used to select the voice for synthesis.</param>
/// <param name="DisplayName">Human-facing name.</param>
/// <param name="ProviderName">The registry TTS name that owns it ("ElevenLabs", "Mistral", "VoiceClone").</param>
/// <param name="Kind">Standard (provider-supplied) or Cloned (user-created).</param>
/// <param name="Language">Optional BCP-47 language tag.</param>
/// <param name="PreviewUrl">Optional provider sample URL — never auto-downloaded.</param>
/// <param name="Description">Optional provider blurb.</param>
public sealed record ProviderVoice(
    string Id,
    string DisplayName,
    string ProviderName,
    VoiceKind Kind,
    string? Language = null,
    string? PreviewUrl = null,
    string? Description = null);

/// <summary>Whether a voice ships with the provider or was created by a user.</summary>
public enum VoiceKind
{
    /// <summary>A provider-supplied stock voice.</summary>
    Standard,

    /// <summary>A voice created from reference audio (this account's, or a local embedding).</summary>
    Cloned,
}

/// <summary>
/// Optional capability a TTS provider may expose: enumerate the voices it can actually use right
/// now. Resolved via <c>VoxaTtsDescriptor.ResolveCatalog</c>; a provider whose voice list is a
/// compiled-in catalog (Piper, Kokoro) simply leaves that resolver null and never implements this.
/// </summary>
public interface IVoiceCatalogProvider
{
    /// <summary>The provider's live voice list. May hit the network; honour the cancellation token.</summary>
    Task<IReadOnlyList<ProviderVoice>> ListVoicesAsync(CancellationToken ct);
}
