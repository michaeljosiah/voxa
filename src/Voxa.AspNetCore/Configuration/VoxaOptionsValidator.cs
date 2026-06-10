using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Voxa.AspNetCore;

/// <summary>
/// Eager (ValidateOnStart) validator for structural correctness: unknown profile names,
/// unknown provider names when a name is given, and per-provider sub-section checks.
/// The Stt/Tts-required checks (needed only for UseDefaults()) run lazily via
/// <see cref="VoxaDefaultsGuard"/> to avoid blocking à-la-carte hosts that compose
/// pipelines manually and never call UseDefaults().
/// </summary>
internal sealed class VoxaOptionsValidator : IValidateOptions<VoxaOptions>
{
    private readonly VoxaProviderRegistry _registry;
    private readonly IConfiguration _configuration;

    public VoxaOptionsValidator(VoxaProviderRegistry registry, IConfiguration configuration)
    {
        _registry = registry;
        _configuration = configuration;
    }

    public ValidateOptionsResult Validate(string? name, VoxaOptions o)
    {
        var errors = new List<string>();

        if (!VoxaProfiles.IsKnown(o.Profile))
            errors.Add($"Voxa:Profile '{o.Profile}' is not a known profile. " +
                       $"Valid values: {string.Join(", ", VoxaProfiles.Names)}.");

        // When a provider name IS given, it must exist in the registry (catches typos early).
        if (o.Stt is not null && !_registry.SttNames.Contains(o.Stt, StringComparer.OrdinalIgnoreCase))
            errors.Add($"Voxa:Stt '{o.Stt}' is not a registered provider. " +
                       $"Registered: {(  _registry.SttNames.Count == 0 ? "(none — did you reference the Voxa meta-package or register descriptors?)" : string.Join(", ", _registry.SttNames))}.");

        if (o.Tts is not null && !_registry.TtsNames.Contains(o.Tts, StringComparer.OrdinalIgnoreCase))
            errors.Add($"Voxa:Tts '{o.Tts}' is not a registered provider. " +
                       $"Registered: {(_registry.TtsNames.Count == 0 ? "(none — did you reference the Voxa meta-package or register descriptors?)" : string.Join(", ", _registry.TtsNames))}.");

        // Delegate per-provider key validation to the descriptor (validates its own sub-section).
        var root = _configuration.GetSection(VoxaOptions.SectionName);
        if (o.Stt is not null && _registry.TryGetStt(o.Stt, out var stt))
            errors.AddRange(stt.Validate(root));
        if (o.Tts is not null && _registry.TryGetTts(o.Tts, out var tts))
            errors.AddRange(tts.Validate(root));

        // "Silero" is always accepted by name (it's the default; the composer falls back to
        // SilenceGate with a warning when its descriptor isn't registered). Custom engines are
        // valid when their descriptor is registered. Comparisons are case-insensitive to match
        // the Profile and provider-name checks above.
        var knownVad = o.Vad.Engine is not null &&
            (string.Equals(o.Vad.Engine, "Silero", StringComparison.OrdinalIgnoreCase)
             || string.Equals(o.Vad.Engine, "SilenceGate", StringComparison.OrdinalIgnoreCase)
             || string.Equals(o.Vad.Engine, "None", StringComparison.OrdinalIgnoreCase)
             || _registry.VadNames.Contains(o.Vad.Engine, StringComparer.OrdinalIgnoreCase));
        if (!knownVad)
        {
            var valid = new[] { "Silero", "SilenceGate", "None" }
                .Union(_registry.VadNames, StringComparer.OrdinalIgnoreCase);
            errors.Add($"Voxa:Vad:Engine '{o.Vad.Engine}' is invalid. " +
                       $"Valid values: {string.Join(", ", valid)}.");
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
