using Microsoft.Extensions.Configuration;
using Voxa.Speech;

namespace Voxa.Speech.AssemblyAI;

/// <summary>
/// Config-driven descriptor for AssemblyAI streaming STT (<c>Voxa:Stt = "AssemblyAI"</c>). Register via
/// <c>VoxaBuilder.AddProvider()</c> or through the Voxa meta-package.
/// </summary>
public static class AssemblyAISpeechDescriptors
{
    public static VoxaSttDescriptor Stt { get; } = new(
        Name: "AssemblyAI",
        ConfigSection: "AssemblyAI",
        PreferredInputSampleRate: 16000,
        Validate: root =>
        {
            var errors = new List<string>();
            if (string.IsNullOrEmpty(root["AssemblyAI:ApiKey"]))
                errors.Add("Voxa:AssemblyAI:ApiKey is required when Voxa:Stt is 'AssemblyAI'.");
            return errors;
        },
        CreateProcessor: (_, root) => new SpeechToTextProcessor(() => new AssemblyAISttEngine(BindOptions(root))));

    internal static AssemblyAIOptions BindOptions(IConfigurationSection root)
    {
        var s = root.GetSection("AssemblyAI");
        return new AssemblyAIOptions
        {
            ApiKey          = s["ApiKey"] ?? string.Empty,
            InputSampleRate = s.GetValue("InputSampleRate", 16000),
            FormatTurns     = s.GetValue("FormatTurns", true),
            ApiBaseUrl      = s["ApiBaseUrl"] ?? "wss://streaming.assemblyai.com/v3/ws",
        };
    }
}
