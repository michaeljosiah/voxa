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
    [property: JsonPropertyName("skipped")] bool? Skipped = null);

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
                : s.Tor.HasValue ? new BaselineCategory(Tor: s.Tor)        // pause_handling (TOR)
                : new BaselineCategory(TtfbP50Ms: s.TtfbP50Ms)));          // latency categories
}

/// <summary>Diffs a scored run against the baseline within tolerance — a regression is a build failure.</summary>
public static class BaselineGate
{
    public static IReadOnlyList<string> Check(IReadOnlyList<CategoryScore> scores, Baseline baseline)
    {
        var regressions = new List<string>();
        foreach (var score in scores)
        {
            if (score.Skipped) continue;
            if (!baseline.Categories.TryGetValue(score.Category, out var b) || b.Skipped == true) continue;

            // pause_handling — TOR is lower-better; a rise beyond tolerance is a regression.
            if (score.Tor is double tor && b.Tor is double bTor && tor - bTor > baseline.Tolerance.Tor)
                regressions.Add(
                    $"{score.Category}: TOR {tor:0.###} regressed from {bTor:0.###} (tolerance {baseline.Tolerance.Tor:0.###}).");

            // smooth_turn_taking / user_interruption — first-word latency must not get slower beyond tolerance.
            if (score.TtfbP50Ms is double ms && b.TtfbP50Ms is double bMs)
            {
                var allowed = Math.Max(bMs * baseline.Tolerance.LatencyMsPct, baseline.Tolerance.LatencyMsAbs);
                if (ms - bMs > allowed)
                    regressions.Add(
                        $"{score.Category}: ttfb_p50 {ms:0.#} ms regressed from {bMs:0.#} ms (allowed +{allowed:0.#} ms).");
            }
        }
        return regressions;
    }
}
