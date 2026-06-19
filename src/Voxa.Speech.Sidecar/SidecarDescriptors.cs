using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Voxa.Speech.Sidecar;

/// <summary>
/// Config-driven descriptor for the sidecar TTS provider: <c>"Voxa:Tts": "Sidecar"</c> (VVL-002).
/// This is the <b>opt-in heavy tier</b> — the meta-package does not register it; a host opts in via
/// <c>AddVoxa(config, voxa =&gt; voxa.AddProvider(SidecarDescriptors.Tts))</c>. Validation is keyless
/// and side-effect-free: it only checks that a sidecar launch target is configured and present.
/// </summary>
public static class SidecarDescriptors
{
    public const string ConfigSectionName = "Sidecar";

    public static VoxaTtsDescriptor Tts { get; } = new(
        Name: "Sidecar",
        ConfigSection: ConfigSectionName,
        OutputSampleRate: 24000,
        Validate: Validate,
        CreateProcessor: (sp, root) =>
        {
            // Label emitted AudioRawFrames with the configured rate (matching ResolveOutputSampleRate and
            // the session envelope), not the processor's 24 kHz default — Codex P2, mirrors the Piper descriptor.
            var options = SidecarOptions.FromConfiguration(root);
            return new TextToSpeechProcessor(
                new SidecarTtsEngine(options, LoggerFor(sp)),
                outputSampleRate: options.OutputSampleRate,
                logger: LoggerFor(sp));
        })
    {
        ResolveOutputSampleRate = root => SidecarOptions.FromConfiguration(root).OutputSampleRate,
    };

    private static IReadOnlyList<string> Validate(IConfigurationSection root)
    {
        var options = SidecarOptions.FromConfiguration(root);
        var errors = new List<string>();

        if (string.IsNullOrEmpty(options.ExecutablePath) && string.IsNullOrEmpty(options.PythonScript))
        {
            errors.Add(
                "Voxa:Sidecar requires either Voxa:Sidecar:ExecutablePath (a built sidecar binary) or " +
                "Voxa:Sidecar:PythonScript (dev mode). VVL-002 ships no pinned frozen binary yet — see the " +
                "Voxa.Speech.Sidecar README to build one.");
        }
        else if (!string.IsNullOrEmpty(options.ExecutablePath) && !File.Exists(options.ExecutablePath))
        {
            errors.Add($"Voxa:Sidecar:ExecutablePath is set to '{options.ExecutablePath}' but no file exists there.");
        }
        else if (!string.IsNullOrEmpty(options.PythonScript) && !File.Exists(options.PythonScript))
        {
            errors.Add($"Voxa:Sidecar:PythonScript is set to '{options.PythonScript}' but no file exists there.");
        }

        return errors;
    }

    private static ILogger? LoggerFor(IServiceProvider sp)
        => (sp.GetService(typeof(ILoggerFactory)) as ILoggerFactory)?.CreateLogger("Voxa.Speech.Sidecar");
}
