using Microsoft.Extensions.Configuration;
using Voxa.Speech;

namespace Voxa.Speech.Speechmatics;

/// <summary>
/// Config-driven descriptor for Speechmatics real-time STT (<c>Voxa:Stt = "Speechmatics"</c>). Register via
/// <c>VoxaBuilder.AddProvider()</c> or through the Voxa meta-package.
/// </summary>
public static class SpeechmaticsSpeechDescriptors
{
    public static VoxaSttDescriptor Stt { get; } = new(
        Name: "Speechmatics",
        ConfigSection: "Speechmatics",
        PreferredInputSampleRate: 16000,
        Validate: root =>
        {
            var errors = new List<string>();
            if (string.IsNullOrEmpty(root["Speechmatics:ApiKey"]))
                errors.Add("Voxa:Speechmatics:ApiKey is required when Voxa:Stt is 'Speechmatics'.");
            return errors;
        },
        CreateProcessor: (_, root) => new SpeechToTextProcessor(() => new SpeechmaticsSttEngine(BindOptions(root))));

    internal static SpeechmaticsOptions BindOptions(IConfigurationSection root)
    {
        var s = root.GetSection("Speechmatics");
        return new SpeechmaticsOptions
        {
            ApiKey          = s["ApiKey"] ?? string.Empty,
            Language        = s["Language"] ?? "en",
            InputSampleRate = s.GetValue("InputSampleRate", 16000),
        };
    }
}
