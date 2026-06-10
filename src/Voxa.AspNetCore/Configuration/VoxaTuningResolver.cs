using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Voxa.AspNetCore;

/// <summary>
/// Effective, fully-resolved tuning after merging explicit config over the active profile.
/// All fields are non-null — the resolver guarantees this.
/// </summary>
public sealed record VoxaEffectiveTuning(
    TimeSpan VadStartDuration,
    TimeSpan VadStopDuration,
    TimeSpan VadPrerollDuration,
    float    VadConfidenceThreshold,
    double   VadMinRms,
    int      EagerFirstChunkMinChars,
    int      MaxBufferChars);

/// <summary>
/// Named latency profile presets. Values encode the VPS-001 performance-tuning.md recommendations
/// for each operating mode — users get competitive latency without learning any knobs.
/// </summary>
public static class VoxaProfiles
{
    public static readonly IReadOnlyList<string> Names = ["Default", "LowLatency", "Quality", "Cheap"];

    public static bool IsKnown(string name) => Names.Contains(name, StringComparer.OrdinalIgnoreCase);

    internal static VoxaEffectiveTuning Get(string name) => name.ToLowerInvariant() switch
    {
        "lowlatency" => new VoxaEffectiveTuning(
            VadStartDuration:       TimeSpan.FromMilliseconds(150),
            VadStopDuration:        TimeSpan.FromMilliseconds(400),
            VadPrerollDuration:     TimeSpan.FromMilliseconds(300),
            VadConfidenceThreshold: 0.5f,
            VadMinRms:              0.003,
            EagerFirstChunkMinChars: 40,
            MaxBufferChars:          350),

        "quality" => new VoxaEffectiveTuning(
            VadStartDuration:       TimeSpan.FromMilliseconds(200),
            VadStopDuration:        TimeSpan.FromMilliseconds(1000),
            VadPrerollDuration:     TimeSpan.FromMilliseconds(400),
            VadConfidenceThreshold: 0.6f,
            VadMinRms:              0.003,
            EagerFirstChunkMinChars: 0,
            MaxBufferChars:          500),

        "cheap" => new VoxaEffectiveTuning(
            VadStartDuration:       TimeSpan.FromMilliseconds(200),
            VadStopDuration:        TimeSpan.FromMilliseconds(800),
            VadPrerollDuration:     TimeSpan.FromMilliseconds(300),
            VadConfidenceThreshold: 0.5f,
            VadMinRms:              0.003,
            EagerFirstChunkMinChars: 40,
            MaxBufferChars:          500),

        _ /* "default" or unknown */ => new VoxaEffectiveTuning(
            VadStartDuration:       TimeSpan.FromMilliseconds(200),
            VadStopDuration:        TimeSpan.FromMilliseconds(800),
            VadPrerollDuration:     TimeSpan.FromMilliseconds(300),
            VadConfidenceThreshold: 0.5f,
            VadMinRms:              0.003,
            EagerFirstChunkMinChars: 0,
            MaxBufferChars:          500),
    };
}

/// <summary>
/// Resolves the effective tuning by merging explicit config (non-null beats everything)
/// over the active profile. Precedence: explicit config > profile > Default profile.
/// </summary>
public sealed class VoxaTuningResolver
{
    private readonly ILogger<VoxaTuningResolver> _logger;

    public VoxaTuningResolver(ILogger<VoxaTuningResolver> logger) => _logger = logger;

    public VoxaEffectiveTuning Resolve(VoxaOptions o)
    {
        var p = VoxaProfiles.Get(o.Profile);

        var preroll = MsOr(o.Vad.PrerollDurationMs, p.VadPrerollDuration);
        var start   = MsOr(o.Vad.StartDurationMs,   p.VadStartDuration);

        if (preroll < start)
        {
            _logger.LogWarning(
                "Voxa: Vad.PrerollDuration ({Preroll} ms) < Vad.StartDuration ({Start} ms); " +
                "clamping preroll to start to satisfy the 'preroll ≥ start' invariant.",
                (int)preroll.TotalMilliseconds, (int)start.TotalMilliseconds);
            preroll = start;
        }

        var result = new VoxaEffectiveTuning(
            VadStartDuration:        start,
            VadStopDuration:         MsOr(o.Vad.StopDurationMs,         p.VadStopDuration),
            VadPrerollDuration:      preroll,
            VadConfidenceThreshold:  o.Vad.ConfidenceThreshold ?? p.VadConfidenceThreshold,
            VadMinRms:               o.Vad.MinRms              ?? p.VadMinRms,
            EagerFirstChunkMinChars: o.Aggregator.EagerFirstChunkMinChars ?? p.EagerFirstChunkMinChars,
            MaxBufferChars:          o.Aggregator.MaxBufferChars          ?? p.MaxBufferChars);

        _logger.LogInformation(
            "Voxa profile '{Profile}': stop={Stop}ms start={Start}ms preroll={Preroll}ms " +
            "eager={Eager} maxBuf={MaxBuf}",
            o.Profile,
            (int)result.VadStopDuration.TotalMilliseconds,
            (int)result.VadStartDuration.TotalMilliseconds,
            (int)result.VadPrerollDuration.TotalMilliseconds,
            result.EagerFirstChunkMinChars,
            result.MaxBufferChars);

        return result;
    }

    private static TimeSpan MsOr(int? ms, TimeSpan fallback)
        => ms is { } v ? TimeSpan.FromMilliseconds(v) : fallback;
}
