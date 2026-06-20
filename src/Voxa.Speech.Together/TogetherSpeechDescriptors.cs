using Microsoft.Extensions.Configuration;
using Voxa.Speech;
using Voxa.Speech.OpenAI;

namespace Voxa.Speech.Together;

/// <summary>
/// Config-driven descriptor for Together AI's OpenAI-compatible Whisper transcription endpoint
/// (<c>Voxa:Stt = "Together"</c>). Together serves Whisper on the same <c>/audio/transcriptions</c>
/// schema as OpenAI, so this reuses the proven <see cref="OpenAIWhisperEngine"/> pointed at Together's
/// base URL — only the credentials, base URL and model name differ.
/// </summary>
public static class TogetherSpeechDescriptors
{
    /// <summary>Together's OpenAI-compatible API base.</summary>
    public const string DefaultApiBaseUrl = "https://api.together.xyz/v1";

    /// <summary>Together's Whisper model id.</summary>
    public const string DefaultModel = "openai/whisper-large-v3";

    public static VoxaSttDescriptor Stt { get; } = new(
        Name: "Together",
        ConfigSection: "Together",
        PreferredInputSampleRate: 16000,
        Validate: root =>
        {
            var errors = new List<string>();
            if (string.IsNullOrEmpty(root["Together:ApiKey"]))
                errors.Add("Voxa:Together:ApiKey is required when Voxa:Stt is 'Together'.");
            return errors;
        },
        CreateProcessor: (sp, root) =>
            OpenAISpeech.StreamingTranscription(BindOptions(root), sp.ResolveHttpClient()));

    // Bind the Voxa:Together section into the reused OpenAI engine's options. Internal so the tests can
    // pin the Together-specific defaults (base URL + model) — the only behaviour this package adds.
    internal static OpenAISpeechOptions BindOptions(IConfigurationSection root)
    {
        var s = root.GetSection("Together");
        return new OpenAISpeechOptions
        {
            ApiKey          = s["ApiKey"] ?? string.Empty,
            ApiBaseUrl      = s["ApiBaseUrl"] ?? DefaultApiBaseUrl,
            SttModel        = s["SttModel"] ?? DefaultModel,
            SttLanguage     = s["SttLanguage"],
            InputSampleRate = s.GetValue("InputSampleRate", 16000),
        };
    }
}
