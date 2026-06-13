namespace Voxa.Studio.Services;

/// <summary>
/// The compile-time list of provider identities Studio can surface in Settings (VST-003 WS2). Keyed by
/// identity, not by role: <c>OpenAI</c> is one entry filling STT + TTS + Agent off a single key. Each
/// <see cref="ProviderManifest.Name"/> matches a registry descriptor name (verified against the live
/// registry by the WS2-A3 test), so the Config dropdown filter never drifts from what AddVoxa registers.
///
/// <para>
/// Adding a provider to Studio's UI is two edits: register a descriptor (so it is in the registry) and
/// add an entry here (so it has a card + fields). A registry name with no manifest is treated as
/// always-available by the Config filter but has no Settings card until one is added.
/// </para>
/// </summary>
public static class ProviderManifestCatalog
{
    private static ProviderFieldDescriptor ApiKey(string identity, string? placeholder = "sk-…") =>
        new("ApiKey", "API Key", placeholder, IsSecret: true, ConfigKey: $"Voxa:{identity}:ApiKey");

    public static IReadOnlyList<ProviderManifest> All { get; } =
    [
        new ProviderManifest(
            Name: "OpenAI",
            DisplayName: "OpenAI",
            Roles: [ProviderRole.Stt, ProviderRole.Tts, ProviderRole.Agent],
            Description: "Whisper speech-to-text, text-to-speech, and the chat agent — one key.",
            IsLocal: false,
            DocsUrl: "https://platform.openai.com/api-keys",
            Fields: [ApiKey("OpenAI")]),

        new ProviderManifest(
            Name: "Azure",
            DisplayName: "Azure Speech",
            Roles: [ProviderRole.Stt, ProviderRole.Tts],
            Description: "Azure Cognitive Services speech-to-text and neural text-to-speech.",
            IsLocal: false,
            DocsUrl: "https://learn.microsoft.com/azure/ai-services/speech-service/",
            Fields:
            [
                new ProviderFieldDescriptor("SubscriptionKey", "Subscription Key", "your Azure Speech key",
                    IsSecret: true, ConfigKey: "Voxa:AzureSpeech:SubscriptionKey"),
                new ProviderFieldDescriptor("Region", "Region", "e.g. eastus",
                    IsSecret: false, ConfigKey: "Voxa:AzureSpeech:Region"),
            ]),

        new ProviderManifest(
            Name: "ElevenLabs",
            DisplayName: "ElevenLabs",
            Roles: [ProviderRole.Tts],
            Description: "High-quality text-to-speech with voice cloning.",
            IsLocal: false,
            DocsUrl: "https://elevenlabs.io/app/settings/api-keys",
            Fields: [ApiKey("ElevenLabs", "sk_…")]),

        new ProviderManifest(
            Name: "Mistral",
            DisplayName: "Mistral",
            Roles: [ProviderRole.Stt, ProviderRole.Tts],
            Description: "Voxtral speech-to-text and Mistral text-to-speech — one key.",
            IsLocal: false,
            DocsUrl: "https://console.mistral.ai/api-keys",
            Fields: [ApiKey("Mistral")]),

        // ── local tier: always available, no credentials ──────────────────────────
        new ProviderManifest(
            Name: "WhisperCpp",
            DisplayName: "Whisper (local)",
            Roles: [ProviderRole.Stt],
            Description: "On-device speech-to-text via whisper.cpp. Runs offline.",
            IsLocal: true, DocsUrl: null, Fields: []),

        new ProviderManifest(
            Name: "Piper",
            DisplayName: "Piper (local)",
            Roles: [ProviderRole.Tts],
            Description: "Fast on-device neural text-to-speech. Runs offline.",
            IsLocal: true, DocsUrl: null, Fields: []),

        new ProviderManifest(
            Name: "Kokoro",
            DisplayName: "Kokoro (local)",
            Roles: [ProviderRole.Tts],
            Description: "On-device text-to-speech with expressive voices. Runs offline.",
            IsLocal: true, DocsUrl: null, Fields: []),

        new ProviderManifest(
            Name: "Echo",
            DisplayName: "Echo (local)",
            Roles: [ProviderRole.Agent],
            Description: "Keyless diagnostic agent that repeats the transcript. No account needed.",
            IsLocal: true, DocsUrl: null, Fields: []),
    ];

    /// <summary>The manifest for a registry name, or null if the identity is not described here.</summary>
    public static ProviderManifest? Find(string name) =>
        All.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
}
