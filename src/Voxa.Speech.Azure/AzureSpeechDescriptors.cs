using Microsoft.Extensions.Configuration;
using Voxa.Speech;

namespace Voxa.Speech.Azure;

/// <summary>
/// Config-driven descriptors for Azure Speech STT and TTS.
/// Register via VoxaBuilder.AddProvider() or through the Voxa meta-package.
/// </summary>
public static class AzureSpeechDescriptors
{
    public static VoxaSttDescriptor Stt { get; } = new(
        Name: "Azure",
        ConfigSection: "AzureSpeech",
        PreferredInputSampleRate: 16000,
        Validate: root =>
        {
            var errors = new List<string>();
            if (string.IsNullOrEmpty(root["AzureSpeech:SubscriptionKey"]))
                errors.Add("Voxa:AzureSpeech:SubscriptionKey is required when Voxa:Stt is 'Azure'.");
            if (string.IsNullOrEmpty(root["AzureSpeech:Region"]))
                errors.Add("Voxa:AzureSpeech:Region is required when Voxa:Stt is 'Azure'.");
            return errors;
        },
        CreateProcessor: (sp, root) => AzureSpeech.StreamingTranscription(BindOptions(root)));

    public static VoxaTtsDescriptor Tts { get; } = new(
        Name: "Azure",
        ConfigSection: "AzureSpeech",
        OutputSampleRate: 24000,
        Validate: root =>
        {
            var errors = new List<string>();
            if (string.IsNullOrEmpty(root["AzureSpeech:SubscriptionKey"]))
                errors.Add("Voxa:AzureSpeech:SubscriptionKey is required when Voxa:Tts is 'Azure'.");
            if (string.IsNullOrEmpty(root["AzureSpeech:Region"]))
                errors.Add("Voxa:AzureSpeech:Region is required when Voxa:Tts is 'Azure'.");
            return errors;
        },
        CreateProcessor: (sp, root) => AzureSpeech.Synthesis(BindOptions(root)));

    private static AzureSpeechOptions BindOptions(IConfigurationSection root)
    {
        var s = root.GetSection("AzureSpeech");
        return new AzureSpeechOptions
        {
            SubscriptionKey     = s["SubscriptionKey"] ?? string.Empty,
            Region              = s["Region"] ?? string.Empty,
            Voice               = s["Voice"] ?? "en-US-JennyNeural",
            RecognitionLanguage = s["RecognitionLanguage"] ?? "en-US",
            InputSampleRate     = s.GetValue("InputSampleRate",  16000),
            InputChannels       = s.GetValue("InputChannels",    1),
            OutputSampleRate    = s.GetValue("OutputSampleRate", 24000),
        };
    }
}
