using Microsoft.Extensions.Configuration;
using Voxa.Speech;

namespace Voxa.Speech.Gladia;

/// <summary>
/// Config-driven descriptor for Gladia live STT (<c>Voxa:Stt = "Gladia"</c>). Register via
/// <c>VoxaBuilder.AddProvider()</c> or through the Voxa meta-package.
/// </summary>
public static class GladiaSpeechDescriptors
{
    public static VoxaSttDescriptor Stt { get; } = new(
        Name: "Gladia",
        ConfigSection: "Gladia",
        PreferredInputSampleRate: 16000,
        Validate: root =>
        {
            var errors = new List<string>();
            if (string.IsNullOrEmpty(root["Gladia:ApiKey"]))
                errors.Add("Voxa:Gladia:ApiKey is required when Voxa:Stt is 'Gladia'.");
            return errors;
        },
        CreateProcessor: (sp, root) => new SpeechToTextProcessor(() => new GladiaSttEngine(BindOptions(root), sp.ResolveHttpClient())));

    internal static GladiaOptions BindOptions(IConfigurationSection root)
    {
        var s = root.GetSection("Gladia");
        return new GladiaOptions
        {
            ApiKey          = s["ApiKey"] ?? string.Empty,
            Language        = s["Language"],
            InputSampleRate = s.GetValue("InputSampleRate", 16000),
            ApiBaseUrl      = s["ApiBaseUrl"] ?? "https://api.gladia.io",
        };
    }
}
