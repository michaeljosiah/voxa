using Microsoft.Extensions.Configuration;
using Voxa.Speech;

namespace Voxa.Speech.Aws;

/// <summary>
/// Config-driven descriptor for AWS Transcribe streaming STT (<c>Voxa:Stt = "Aws"</c>). Register via
/// <c>VoxaBuilder.AddProvider()</c> or through the Voxa meta-package.
/// </summary>
public static class AwsSpeechDescriptors
{
    public static VoxaSttDescriptor Stt { get; } = new(
        Name: "Aws",
        ConfigSection: "Aws",
        PreferredInputSampleRate: 16000,
        Validate: root =>
        {
            var errors = new List<string>();
            // AWS resolves credentials from the default chain (env / profile / IAM role) when keys are omitted;
            // only flag a half-supplied explicit pair.
            bool hasId = !string.IsNullOrEmpty(root["Aws:AccessKeyId"]);
            bool hasSecret = !string.IsNullOrEmpty(root["Aws:SecretAccessKey"]);
            if (hasId ^ hasSecret)
                errors.Add("Voxa:Aws:AccessKeyId and Voxa:Aws:SecretAccessKey must be set together (or both omitted to use the default AWS credential chain).");
            return errors;
        },
        CreateProcessor: (_, root) => new SpeechToTextProcessor(() => new AwsSttEngine(BindOptions(root))));

    internal static AwsSpeechOptions BindOptions(IConfigurationSection root)
    {
        var s = root.GetSection("Aws");
        return new AwsSpeechOptions
        {
            Region          = s["Region"] ?? "us-east-1",
            AccessKeyId     = s["AccessKeyId"],
            SecretAccessKey = s["SecretAccessKey"],
            Language        = s["Language"] ?? "en-US",
            InputSampleRate = s.GetValue("InputSampleRate", 16000),
        };
    }
}
