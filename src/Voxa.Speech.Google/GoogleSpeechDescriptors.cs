using Microsoft.Extensions.Configuration;
using Voxa.Speech;

namespace Voxa.Speech.Google;

/// <summary>
/// Config-driven descriptor for Google Cloud Speech-to-Text v2 (<c>Voxa:Stt = "Google"</c>). Register via
/// <c>VoxaBuilder.AddProvider()</c> or through the Voxa meta-package.
/// </summary>
public static class GoogleSpeechDescriptors
{
    public static VoxaSttDescriptor Stt { get; } = new(
        Name: "Google",
        ConfigSection: "Google",
        PreferredInputSampleRate: 16000,
        Validate: root =>
        {
            var errors = new List<string>();
            if (string.IsNullOrEmpty(root["Google:ProjectId"]))
                errors.Add("Voxa:Google:ProjectId is required when Voxa:Stt is 'Google'.");
            return errors;
        },
        CreateProcessor: (_, root) => new SpeechToTextProcessor(() => new GoogleSttEngine(BindOptions(root))));

    internal static GoogleSpeechOptions BindOptions(IConfigurationSection root)
    {
        var s = root.GetSection("Google");
        return new GoogleSpeechOptions
        {
            ProjectId       = s["ProjectId"] ?? string.Empty,
            Location        = s["Location"] ?? "global",
            Recognizer      = s["Recognizer"] ?? "_",
            Language        = s["Language"] ?? "en-US",
            Model           = s["Model"] ?? "long",
            InputSampleRate = s.GetValue("InputSampleRate", 16000),
            CredentialsPath = s["CredentialsPath"],
            CredentialsJson = s["CredentialsJson"],
        };
    }
}
