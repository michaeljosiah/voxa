namespace Voxa.Speech.Voices;

/// <summary>
/// A reference clip for cloning. Bytes rather than a path, because the provider may be remote and
/// the caller owns the file IO. Assumed PCM16 mono WAV unless <see cref="Mime"/> says otherwise.
/// </summary>
public sealed record VoiceSample(string FileName, ReadOnlyMemory<byte> Data, string Mime = "audio/wav");

/// <summary>Everything a provider needs to mint a new voice from reference audio.</summary>
public sealed record VoiceCloneRequest(
    string Name,
    IReadOnlyList<VoiceSample> Samples,
    string? Description = null,
    string? Language = null);

/// <summary>
/// Optional capability a TTS provider may expose: create a voice from samples. Resolved via
/// <c>VoxaTtsDescriptor.ResolveCloner</c>. Implementations are the transport only — the consent
/// gate lives in the host (Studio), which must not call these without recorded user attestation.
/// </summary>
public interface IVoiceCloneProvider
{
    /// <summary>Enroll a new voice; returns it as it now appears in the provider's catalog.</summary>
    Task<ProviderVoice> CreateVoiceAsync(VoiceCloneRequest request, CancellationToken ct);

    /// <summary>Remove a previously created voice by its provider-native id.</summary>
    Task DeleteVoiceAsync(string voiceId, CancellationToken ct);
}
