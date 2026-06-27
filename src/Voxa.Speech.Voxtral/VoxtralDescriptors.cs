using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Voxa.Speech.Voxtral;

/// <summary>
/// Config-driven descriptor for local Voxtral realtime STT: <c>"Voxa:Stt": "Voxtral"</c> (VLS-009).
/// Registered in the meta-package so the string resolves everywhere; Studio additionally GPU-gates it as the
/// default. Validation is <b>keyless and side-effect-free</b> (no GPU probe, no network) — it only checks that a
/// hosting mode is resolvable and the audio knobs are in range, mirroring the VVL-002 sidecar descriptor.
/// </summary>
public static class VoxtralDescriptors
{
    public const string ConfigSectionName = "Voxtral";

    public static VoxaSttDescriptor Stt { get; } = new(
        Name: "Voxtral",
        ConfigSection: ConfigSectionName,
        PreferredInputSampleRate: 16000,
        Validate: Validate,
        CreateProcessor: (sp, root) =>
        {
            var options = VoxtralOptions.FromConfiguration(root);
            var logger = LoggerFor<VoxtralRealtimeSttEngine>(sp);
            return new SpeechToTextProcessor(() => new VoxtralRealtimeSttEngine(options, logger));
        });

    private static IReadOnlyList<string> Validate(IConfigurationSection root)
    {
        var errors = new List<string>();
        var o = VoxtralOptions.FromConfiguration(root);

        if (!o.HasHostingMode)
            errors.Add(
                "Voxa:Voxtral needs a hosting mode when Voxa:Stt is 'Voxtral': set Voxa:Voxtral:ServerUrl to a " +
                "running vLLM realtime server, or Voxa:Voxtral:LaunchCommand/ExecutablePath to have Voxa start one.");

        // An explicit launcher executable must exist (keyless, side-effect-free). A bare LaunchCommand resolved
        // from PATH (e.g. "vllm") isn't file-checked here — that surfaces at launch, like the whisper.cpp model.
        if (!string.IsNullOrEmpty(o.ExecutablePath) && !File.Exists(o.ExecutablePath))
            errors.Add($"Voxa:Voxtral:ExecutablePath is set to '{o.ExecutablePath}' but no file exists there.");

        if (o.InputSampleRate != 16000)
            errors.Add(
                $"Voxa:Voxtral:InputSampleRate is {o.InputSampleRate}, but Voxtral Realtime requires 16000 Hz mono PCM16.");

        if (o.DelayMs is < 80 or > 2400)
            errors.Add($"Voxa:Voxtral:DelayMs is {o.DelayMs} ms, but the model supports 80–2400 ms.");

        return errors;
    }

    private static ILogger LoggerFor<T>(IServiceProvider sp)
        => (sp.GetService(typeof(ILoggerFactory)) as ILoggerFactory)?.CreateLogger(typeof(T).FullName!)
           ?? NullLogger.Instance;
}
