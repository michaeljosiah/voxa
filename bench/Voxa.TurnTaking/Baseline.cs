using System.Text.Json;
using System.Text.Json.Serialization;

namespace Voxa.TurnTaking;

/// <summary>Regression tolerance. A latency change must exceed BOTH the relative and absolute floors to fail
/// the gate — the absolute floor keeps the sub-millisecond mock-engine timings from flapping the build.</summary>
public sealed record BaselineTolerance(
    [property: JsonPropertyName("tor")] double Tor,
    [property: JsonPropertyName("latency_ms_pct")] double LatencyMsPct,
    [property: JsonPropertyName("latency_ms_abs")] double LatencyMsAbs);

public sealed record BaselineCategory(
    [property: JsonPropertyName("tor")] double? Tor = null,
    [property: JsonPropertyName("ttfb_p50_ms")] double? TtfbP50Ms = null,
    [property: JsonPropertyName("barge_in_yield_p50_ms")] double? BargeInYieldP50Ms = null,
    [property: JsonPropertyName("skipped")] bool? Skipped = null,
    [property: JsonPropertyName("note")] string? Note = null);

/// <summary>
/// The checked-in regression reference — per category, the TOR / latency the pipeline achieves on the mini
/// fixture with mock engines. The smoke test diffs a fresh run against this within tolerance, so a turn-taking
/// regression fails the build. Refreshing it is a deliberate, reviewed change (a knob moved, a category
/// improved) — never a silent overwrite, the same discipline as bumping a model SHA-pin (VRT-001 §7).
/// </summary>
public sealed record Baseline(
    [property: JsonPropertyName("fixture")] string Fixture,
    [property: JsonPropertyName("engines")] string Engines,
    [property: JsonPropertyName("tolerance")] BaselineTolerance Tolerance,
    [property: JsonPropertyName("categories")] Dictionary<string, BaselineCategory> Categories)
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static Baseline Load(string path)
        => JsonSerializer.Deserialize<Baseline>(File.ReadAllText(path), Opts)
           ?? throw new InvalidOperationException($"Could not parse baseline {path}.");

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Opts) + "\n");

    /// <summary>Build a baseline from a scored run (used by <c>--write-baseline</c>).</summary>
    public static Baseline FromScores(string fixture, string engines, IReadOnlyList<CategoryScore> scores) => new(
        fixture, engines,
        new BaselineTolerance(Tor: 0.05, LatencyMsPct: 0.20, LatencyMsAbs: 50.0),
        scores.ToDictionary(
            s => s.Category,
            s => s.Skipped ? new BaselineCategory(Skipped: true)
                : s.Tor.HasValue ? new BaselineCategory(Tor: s.Tor)                              // pause_handling (TOR)
                : s.TtfbP50Ms.HasValue ? new BaselineCategory(TtfbP50Ms: s.TtfbP50Ms)            // smooth_turn_taking
                : s.BargeInYieldP50Ms.HasValue ? new BaselineCategory(BargeInYieldP50Ms: s.BargeInYieldP50Ms) // user_interruption
                : new BaselineCategory(Note: "not exercised offline: barge-in yield needs a real-time/duplex source")));
}

/// <summary>
/// Diffs a scored run against the baseline within tolerance — a regression is a build failure. Iterates the
/// <b>baseline</b> (the contract of what must be measured), not the run, so a category the baseline requires
/// but the run didn't produce — an empty/filtered corpus, or a metric that went <c>null</c> — fails the gate
/// rather than letting it go green without exercising the benchmark. A <c>skipped</c> or note-only baseline
/// entry (e.g. an offline-unexercised category) requires nothing.
/// </summary>
public static class BaselineGate
{
    public static IReadOnlyList<string> Check(IReadOnlyList<CategoryScore> scores, Baseline baseline)
    {
        var regressions = new List<string>();
        var byCategory = scores.ToDictionary(s => s.Category, StringComparer.OrdinalIgnoreCase);

        foreach (var (category, b) in baseline.Categories)
        {
            if (b.Skipped == true) continue;                       // not required to produce a number
            byCategory.TryGetValue(category, out var score);

            if (b.Tor is double bTor)
            {
                if (score?.Tor is not double tor)
                    regressions.Add($"{category}: TOR missing from the run (baseline requires it).");
                else if (tor - bTor > baseline.Tolerance.Tor)
                    regressions.Add($"{category}: TOR {tor:0.###} regressed from {bTor:0.###} (tolerance {baseline.Tolerance.Tor:0.###}).");
            }
            else if (b.TtfbP50Ms is double bMs)
            {
                if (score?.TtfbP50Ms is not double ms)
                    regressions.Add($"{category}: ttfb_p50 missing from the run (baseline requires it).");
                else if (ms - bMs > Math.Max(bMs * baseline.Tolerance.LatencyMsPct, baseline.Tolerance.LatencyMsAbs))
                    regressions.Add($"{category}: ttfb_p50 {ms:0.#} ms regressed from {bMs:0.#} ms (allowed +{Math.Max(bMs * baseline.Tolerance.LatencyMsPct, baseline.Tolerance.LatencyMsAbs):0.#} ms).");
            }
            else if (b.BargeInYieldP50Ms is double bY)
            {
                if (score?.BargeInYieldP50Ms is not double y)
                    regressions.Add($"{category}: barge-in yield missing from the run (baseline requires it).");
                else if (y - bY > Math.Max(bY * baseline.Tolerance.LatencyMsPct, baseline.Tolerance.LatencyMsAbs))
                    regressions.Add($"{category}: barge-in yield {y:0.#} ms regressed from {bY:0.#} ms (allowed +{Math.Max(bY * baseline.Tolerance.LatencyMsPct, baseline.Tolerance.LatencyMsAbs):0.#} ms).");
            }
            // else: a note-only entry (not exercised by design) requires nothing.
        }
        return regressions;
    }
}
