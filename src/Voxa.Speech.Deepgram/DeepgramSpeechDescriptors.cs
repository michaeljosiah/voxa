using Microsoft.Extensions.Configuration;
using Voxa.Speech;

namespace Voxa.Speech.Deepgram;

/// <summary>
/// Config-driven descriptor for Deepgram streaming STT (<c>Voxa:Stt = "Deepgram"</c>). Register via
/// <c>VoxaBuilder.AddProvider()</c> or through the Voxa meta-package.
/// </summary>
public static class DeepgramSpeechDescriptors
{
    public static VoxaSttDescriptor Stt { get; } = new(
        Name: "Deepgram",
        ConfigSection: "Deepgram",
        PreferredInputSampleRate: 16000,
        Validate: root =>
        {
            var errors = new List<string>();
            if (string.IsNullOrEmpty(root["Deepgram:ApiKey"]))
                errors.Add("Voxa:Deepgram:ApiKey is required when Voxa:Stt is 'Deepgram'.");
            return errors;
        },
        CreateProcessor: (_, root) => new SpeechToTextProcessor(() => new DeepgramSttEngine(BindOptions(root))));

    internal static DeepgramOptions BindOptions(IConfigurationSection root)
    {
        var s = root.GetSection("Deepgram");
        return new DeepgramOptions
        {
            ApiKey          = s["ApiKey"] ?? string.Empty,
            Model           = s["Model"] ?? "nova-3",
            Language        = s["Language"],
            InputSampleRate = s.GetValue("InputSampleRate", 16000),
            ApiBaseUrl      = s["ApiBaseUrl"] ?? "wss://api.deepgram.com/v1/listen",
        };
    }
}
