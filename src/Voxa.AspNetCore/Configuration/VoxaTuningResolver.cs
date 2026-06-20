using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Voxa.AspNetCore;

/// <summary>
/// Effective, fully-resolved tuning after merging explicit config over the active profile.
/// The non-nullable fields are always populated; the nullable knobs (VRT-002) are opt-in and
/// stay <c>null</c> unless a profile or explicit config turns them on.
/// </summary>
public sealed record VoxaEffectiveTuning(
    TimeSpan VadStartDuration,
    TimeSpan VadStopDuration,
    TimeSpan VadPrerollDuration,
    float    VadConfidenceThreshold,
    double   VadMinRms,
    int      EagerFirstChunkMinChars,
    int      MaxBufferChars,
    TimeSpan? VadEagerSttDelay = null,
    TimeSpan? VadMaxUtteranceDuration = null,
    TimeSpan? MaxResponseDuration = null);

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
            VadStartDuration:        TimeSpan.FromMilliseconds(150),
            VadStopDuration:         TimeSpan.FromMilliseconds(400),
            VadPrerollDuration:      TimeSpan.FromMilliseconds(300),
            VadConfidenceThreshold:  0.5f,
            VadMinRms:               0.003,
            EagerFirstChunkMinChars: 40,
            MaxBufferChars:          350,
            // VRT-002: eager STT ≈ StopDuration − 250 ms; bound very long speech and runaway responses.
            VadEagerSttDelay:        TimeSpan.FromMilliseconds(150),
            VadMaxUtteranceDuration: TimeSpan.FromSeconds(20),
            MaxResponseDuration:     TimeSpan.FromSeconds(30)),

        "quality" => new VoxaEffectiveTuning(
            VadStartDuration:        TimeSpan.FromMilliseconds(200),
            VadStopDuration:         TimeSpan.FromMilliseconds(1000),
            VadPrerollDuration:      TimeSpan.FromMilliseconds(400),
            VadConfidenceThreshold:  0.6f,
            VadMinRms:               0.003,
            EagerFirstChunkMinChars: 0,
            MaxBufferChars:          500,
            // VRT-002: Quality favours accuracy/completeness — eager off, no caps.
            VadEagerSttDelay:        null,
            VadMaxUtteranceDuration: null,
            MaxResponseDuration:     null),

        "cheap" => new VoxaEffectiveTuning(
            VadStartDuration:        TimeSpan.FromMilliseconds(200),
            VadStopDuration:         TimeSpan.FromMilliseconds(800),
            VadPrerollDuration:      TimeSpan.FromMilliseconds(300),
            VadConfidenceThreshold:  0.5f,
            VadMinRms:               0.003,
            EagerFirstChunkMinChars: 40,
            MaxBufferChars:          500,
            // VRT-002: eager STT ≈ StopDuration − 300 ms; same robustness caps as LowLatency.
            VadEagerSttDelay:        TimeSpan.FromMilliseconds(500),
            VadMaxUtteranceDuration: TimeSpan.FromSeconds(20),
            MaxResponseDuration:     TimeSpan.FromSeconds(30)),

        _ /* "default" or unknown */ => new VoxaEffectiveTuning(
            VadStartDuration:        TimeSpan.FromMilliseconds(200),
            VadStopDuration:         TimeSpan.FromMilliseconds(800),
            VadPrerollDuration:      TimeSpan.FromMilliseconds(300),
            VadConfidenceThreshold:  0.5f,
            VadMinRms:               0.003,
            EagerFirstChunkMinChars: 0,
            MaxBufferChars:          500,
            // VRT-002: every robustness knob is OFF in Default so the composed pipeline stays
            // byte-identical to pre-VRT-002 (the defaults-byte-identity golden gate).
            VadEagerSttDelay:        null,
            VadMaxUtteranceDuration: null,
            MaxResponseDuration:     null),
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
        var stop    = MsOr(o.Vad.StopDurationMs,     p.VadStopDuration);

        if (preroll < start)
        {
            _logger.LogWarning(
                "Voxa: Vad.PrerollDuration ({Preroll} ms) < Vad.StartDuration ({Start} ms); " +
                "clamping preroll to start to satisfy the 'preroll ≥ start' invariant.",
                (int)preroll.TotalMilliseconds, (int)start.TotalMilliseconds);
            preroll = start;
        }

        // VRT-002 WS1: eager STT only makes sense strictly before the gate closes. A misconfigured
        // EagerSttDelay ≥ StopDuration is disabled (with a warning) — the VAD also guards this at runtime.
        var eager = NullableMsOr(o.Vad.EagerSttDelayMs, p.VadEagerSttDelay);
        if (eager is { } e && e >= stop)
        {
            _logger.LogWarning(
                "Voxa: Vad.EagerSttDelay ({Eager} ms) ≥ Vad.StopDuration ({Stop} ms); disabling eager STT " +
                "(it must fire strictly before the gate closes).",
                (int)e.TotalMilliseconds, (int)stop.TotalMilliseconds);
            eager = null;
        }

        var result = new VoxaEffectiveTuning(
            VadStartDuration:        start,
            VadStopDuration:         stop,
            VadPrerollDuration:      preroll,
            VadConfidenceThreshold:  o.Vad.ConfidenceThreshold ?? p.VadConfidenceThreshold,
            VadMinRms:               o.Vad.MinRms              ?? p.VadMinRms,
            EagerFirstChunkMinChars: o.Aggregator.EagerFirstChunkMinChars ?? p.EagerFirstChunkMinChars,
            MaxBufferChars:          o.Aggregator.MaxBufferChars          ?? p.MaxBufferChars,
            VadEagerSttDelay:        eager,
            VadMaxUtteranceDuration: NullableMsOr(o.Vad.MaxUtteranceDurationMs, p.VadMaxUtteranceDuration),
            MaxResponseDuration:     NullableMsOr(o.Agent.MaxResponseDurationMs, p.MaxResponseDuration));

        _logger.LogInformation(
            "Voxa profile '{Profile}': stop={Stop}ms start={Start}ms preroll={Preroll}ms " +
            "eager={Eager} maxBuf={MaxBuf} eagerStt={EagerStt} maxUtt={MaxUtt} maxResp={MaxResp}",
            o.Profile,
            (int)result.VadStopDuration.TotalMilliseconds,
            (int)result.VadStartDuration.TotalMilliseconds,
            (int)result.VadPrerollDuration.TotalMilliseconds,
            result.EagerFirstChunkMinChars,
            result.MaxBufferChars,
            result.VadEagerSttDelay is { } es ? $"{(int)es.TotalMilliseconds}ms" : "off",
            result.VadMaxUtteranceDuration is { } mu ? $"{(int)mu.TotalMilliseconds}ms" : "off",
            result.MaxResponseDuration is { } mr ? $"{(int)mr.TotalMilliseconds}ms" : "off");

        return result;
    }

    private static TimeSpan MsOr(int? ms, TimeSpan fallback)
        => ms is { } v ? TimeSpan.FromMilliseconds(v) : fallback;

    private static TimeSpan? NullableMsOr(int? ms, TimeSpan? fallback)
        => ms is { } v ? TimeSpan.FromMilliseconds(v) : fallback;
}
