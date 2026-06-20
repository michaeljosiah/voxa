using Microsoft.Extensions.Configuration;
using Voxa.Speech;

namespace Voxa.Speech.OpenAI;

/// <summary>
/// Config-driven descriptors for OpenAI Whisper (STT) and OpenAI TTS.
/// Register via VoxaBuilder.AddProvider() or through the Voxa meta-package.
/// </summary>
public static class OpenAISpeechDescriptors
{
    public static VoxaSttDescriptor Stt { get; } = new(
        Name: "OpenAI",
        ConfigSection: "OpenAI",
        PreferredInputSampleRate: 16000,
        Validate: root =>
        {
            var errors = new List<string>();
            if (string.IsNullOrEmpty(root["OpenAI:ApiKey"]))
                errors.Add("Voxa:OpenAI:ApiKey is required when Voxa:Stt is 'OpenAI'.");
            return errors;
        },
        CreateProcessor: (sp, root) =>
            OpenAISpeech.StreamingTranscription(BindOptions(root), ResolveHttpClient(sp)));

    public static VoxaTtsDescriptor Tts { get; } = new(
        Name: "OpenAI",
        ConfigSection: "OpenAI",
        OutputSampleRate: 24000,
        Validate: root =>
        {
            var errors = new List<string>();
            if (string.IsNullOrEmpty(root["OpenAI:ApiKey"]))
                errors.Add("Voxa:OpenAI:ApiKey is required when Voxa:Tts is 'OpenAI'.");
            return errors;
        },
        CreateProcessor: (sp, root) =>
            OpenAISpeech.Synthesis(BindOptions(root), ResolveHttpClient(sp)));

    private static OpenAISpeechOptions BindOptions(IConfigurationSection root)
    {
        var s = root.GetSection("OpenAI");
        return new OpenAISpeechOptions
        {
            ApiKey           = s["ApiKey"] ?? string.Empty,
            ApiBaseUrl       = s["ApiBaseUrl"] ?? "https://api.openai.com/v1",
            TtsModel         = s["TtsModel"] ?? "tts-1",
            TtsVoice         = s["TtsVoice"] ?? "alloy",
            SttModel         = s["SttModel"] ?? "whisper-1",
            SttLanguage      = s["SttLanguage"],
            InputSampleRate  = s.GetValue("InputSampleRate",  16000),
            OutputSampleRate = s.GetValue("OutputSampleRate", 24000),
        };
    }

    private static HttpClient? ResolveHttpClient(IServiceProvider sp)
        => sp.ResolveHttpClient();
}
