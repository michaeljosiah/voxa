using System.Text.Json;
using System.Text.Json.Serialization;
using Voxa.Speech.Voices;

namespace Voxa.Studio.Services;

/// <summary>
/// A library entry (VVL-001 WS4): a pointer to a provider voice plus local provenance — never a
/// secret. The authority on whether the voice is usable right now is the provider's live list
/// (§4.2); this record is the curated, named, annotated view, and the original reference samples
/// for a clone. Persisted as one JSON file per profile under <c>~/voxa-voices</c>.
/// </summary>
public sealed record VoiceProfile
{
    /// <summary>Voxa-local id (used for the file name); distinct from the provider's voice id.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string DisplayName { get; init; } = "";

    /// <summary>The registry TTS name that owns it: "ElevenLabs" | "Mistral" | "VoiceClone".</summary>
    public string ProviderName { get; init; } = "";

    /// <summary>The provider-native voice id used to select it for synthesis.</summary>
    public string ProviderVoiceId { get; init; } = "";

    public VoiceKind Kind { get; init; } = VoiceKind.Cloned;

    public string? Language { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>Set ⇔ this profile was cloned with recorded user consent (WS5 gate).</summary>
    public DateTimeOffset? ConsentAttestedAt { get; init; }

    /// <summary>The user's own reference clips, stored under the profile's samples dir.</summary>
    public IReadOnlyList<string> SamplePaths { get; init; } = [];

    public string? Notes { get; init; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static VoiceProfile FromJson(string json) =>
        JsonSerializer.Deserialize<VoiceProfile>(json, JsonOptions)
        ?? throw new InvalidOperationException("Empty voice profile JSON.");
}
