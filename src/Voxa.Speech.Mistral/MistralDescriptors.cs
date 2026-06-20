using Microsoft.Extensions.Configuration;
using Voxa.Speech;

namespace Voxa.Speech.Mistral;

/// <summary>
/// Config-driven descriptor for Mistral TTS.
/// Register via VoxaBuilder.AddProvider() or through the Voxa meta-package.
/// </summary>
public static class MistralDescriptors
{
    public static VoxaTtsDescriptor Tts { get; } = new(
        Name: "Mistral",
        ConfigSection: "Mistral",
        OutputSampleRate: 24000,
        Validate: root =>
        {
            var errors = new List<string>();
            if (string.IsNullOrEmpty(root["Mistral:ApiKey"]))
                errors.Add("Voxa:Mistral:ApiKey is required when Voxa:Tts is 'Mistral'.");
            return errors;
        },
        CreateProcessor: (sp, root) =>
            Mistral.Synthesis(BindOptions(root), ResolveHttpClient(sp)))
    {
        // Voice catalog + cloning (VVL-001 WS2) — only the API key is needed for these.
        ResolveCatalog = (sp, root) => new MistralVoiceCatalog(BindOptions(root), ResolveHttpClient(sp)),
        ResolveCloner  = (sp, root) => new MistralVoiceCatalog(BindOptions(root), ResolveHttpClient(sp)),
    };

    /// <summary>Voxtral speech-to-text (VVL-001 WS2). Utterance-buffered REST transcription.</summary>
    public static VoxaSttDescriptor Stt { get; } = new(
        Name: "Mistral",
        ConfigSection: "Mistral",
        PreferredInputSampleRate: 16000,
        Validate: root =>
        {
            var errors = new List<string>();
            if (string.IsNullOrEmpty(root["Mistral:ApiKey"]))
                errors.Add("Voxa:Mistral:ApiKey is required when Voxa:Stt is 'Mistral'.");
            return errors;
        },
        CreateProcessor: (sp, root) =>
            new SpeechToTextProcessor(new MistralSpeechToTextEngine(BindOptions(root), ResolveHttpClient(sp))));

    private static MistralSpeechOptions BindOptions(IConfigurationSection root)
    {
        var s = root.GetSection("Mistral");
        return new MistralSpeechOptions
        {
            ApiKey           = s["ApiKey"] ?? string.Empty,
            ApiBaseUrl       = s["ApiBaseUrl"] ?? "https://api.mistral.ai/v1",
            Model            = s["Model"] ?? "voxtral-tts",
            Voice            = s["Voice"] ?? "alloy",
            OutputSampleRate = s.GetValue("OutputSampleRate", 24000),
            SttModel         = s["SttModel"] ?? "voxtral-mini-latest",
            InputSampleRate  = s.GetValue("InputSampleRate", 16000),
            SttLanguage      = s["SttLanguage"],
            SttBufferSeconds = s.GetValue("SttBufferSeconds", 30.0),
        };
    }

    private static HttpClient? ResolveHttpClient(IServiceProvider sp)
        => sp.ResolveHttpClient();
}
