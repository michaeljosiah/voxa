namespace Voxa.Speech.Mistral;

/// <summary>
/// Configuration for Mistral's Voxtral-TTS audio API. The endpoint is OpenAI-compatible
/// (<c>/v1/audio/speech</c>), so override <see cref="ApiBaseUrl"/> if Mistral moves the route.
/// </summary>
public sealed record MistralSpeechOptions
{
    /// <summary>Mistral API key — sent as <c>Authorization: Bearer &lt;key&gt;</c>.</summary>
    public required string ApiKey { get; init; }

    /// <summary>Base URL for the API. Defaults to Mistral's public endpoint.</summary>
    public string ApiBaseUrl { get; init; } = "https://api.mistral.ai/v1";

    /// <summary>TTS model id (Mistral's Voxtral family).</summary>
    public string Model { get; init; } = "voxtral-tts";

    /// <summary>Voice id — either a built-in voice or a cloned voice profile id.</summary>
    public string Voice { get; init; } = "alloy";

    /// <summary>Output sample rate for PCM.</summary>
    public int OutputSampleRate { get; init; } = 24000;
}
