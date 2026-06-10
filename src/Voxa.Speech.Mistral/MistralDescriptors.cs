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
            Mistral.Synthesis(BindOptions(root), ResolveHttpClient(sp)));

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
        };
    }

    private static HttpClient? ResolveHttpClient(IServiceProvider sp)
        => (sp.GetService(typeof(IVoxaHttpClientProvider)) as IVoxaHttpClientProvider)?.Resolve();
}
