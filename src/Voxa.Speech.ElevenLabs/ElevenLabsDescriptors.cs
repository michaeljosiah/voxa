using Microsoft.Extensions.Configuration;
using Voxa.Speech;

namespace Voxa.Speech.ElevenLabs;

/// <summary>
/// Config-driven descriptor for ElevenLabs TTS.
/// Register via VoxaBuilder.AddProvider() or through the Voxa meta-package.
/// </summary>
public static class ElevenLabsDescriptors
{
    public static VoxaTtsDescriptor Tts { get; } = new(
        Name: "ElevenLabs",
        ConfigSection: "ElevenLabs",
        OutputSampleRate: 24000,
        Validate: root =>
        {
            var errors = new List<string>();
            if (string.IsNullOrEmpty(root["ElevenLabs:ApiKey"]))
                errors.Add("Voxa:ElevenLabs:ApiKey is required when Voxa:Tts is 'ElevenLabs'.");
            if (string.IsNullOrEmpty(root["ElevenLabs:VoiceId"]))
                errors.Add("Voxa:ElevenLabs:VoiceId is required when Voxa:Tts is 'ElevenLabs'.");
            return errors;
        },
        CreateProcessor: (sp, root) =>
            ElevenLabs.Synthesis(BindOptions(root), ResolveHttpClient(sp)));

    private static ElevenLabsOptions BindOptions(IConfigurationSection root)
    {
        var s = root.GetSection("ElevenLabs");
        return new ElevenLabsOptions
        {
            ApiKey           = s["ApiKey"] ?? string.Empty,
            VoiceId          = s["VoiceId"] ?? string.Empty,
            ApiBaseUrl       = s["ApiBaseUrl"] ?? "https://api.elevenlabs.io/v1",
            ModelId          = s["ModelId"] ?? "eleven_multilingual_v2",
            OutputSampleRate = s.GetValue("OutputSampleRate", 24000),
        };
    }

    private static HttpClient? ResolveHttpClient(IServiceProvider sp)
        => (sp.GetService(typeof(IVoxaHttpClientProvider)) as IVoxaHttpClientProvider)?.Resolve();
}
