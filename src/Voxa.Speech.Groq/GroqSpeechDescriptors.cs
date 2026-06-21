using Microsoft.Extensions.Configuration;
using Voxa.Speech;
using Voxa.Speech.OpenAI;

namespace Voxa.Speech.Groq;

/// <summary>
/// Config-driven descriptor for Groq's OpenAI-compatible Whisper transcription endpoint
/// (<c>Voxa:Stt = "Groq"</c>). Groq serves Whisper on the same <c>/audio/transcriptions</c> schema as
/// OpenAI, so this reuses the proven <see cref="OpenAIWhisperEngine"/> pointed at Groq's base URL —
/// only the credentials, base URL and model name differ. Register via <c>VoxaBuilder.AddProvider()</c>
/// or through the Voxa meta-package.
/// </summary>
public static class GroqSpeechDescriptors
{
    /// <summary>Groq's OpenAI-compatible API base.</summary>
    public const string DefaultApiBaseUrl = "https://api.groq.com/openai/v1";

    /// <summary>Groq's fast Whisper variant (also <c>whisper-large-v3</c>, <c>distil-whisper-large-v3-en</c>).</summary>
    public const string DefaultModel = "whisper-large-v3-turbo";

    public static VoxaSttDescriptor Stt { get; } = new(
        Name: "Groq",
        ConfigSection: "Groq",
        PreferredInputSampleRate: 16000,
        Validate: root =>
        {
            var errors = new List<string>();
            if (string.IsNullOrEmpty(root["Groq:ApiKey"]))
                errors.Add("Voxa:Groq:ApiKey is required when Voxa:Stt is 'Groq'.");
            return errors;
        },
        CreateProcessor: (sp, root) =>
            OpenAISpeech.StreamingTranscription(BindOptions(root), sp.ResolveHttpClient()));

    // Bind the Voxa:Groq section into the reused OpenAI engine's options. Internal so the tests can pin
    // the Groq-specific defaults (base URL + model) — the only behaviour this package adds over OpenAI.
    internal static OpenAISpeechOptions BindOptions(IConfigurationSection root)
    {
        var s = root.GetSection("Groq");
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
