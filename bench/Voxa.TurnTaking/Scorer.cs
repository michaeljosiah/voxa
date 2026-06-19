using System.Text.Json.Serialization;

namespace Voxa.TurnTaking;

/// <summary>
/// Per-category direction-aware score (VRT-001 §7). <c>pause_handling</c> → a turn-offset-rate (lower is
/// better: silence through a thinking-pause is the win). The other two → first-word latency (the ttfb p50)
/// plus a bounded <c>responsiveness</c> in (0,1] so "higher is better" reads consistently in the report.
/// </summary>
public sealed record CategoryScore(
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("tor")] double? Tor,
    [property: JsonPropertyName("ttfb_p50_ms")] double? TtfbP50Ms,
    [property: JsonPropertyName("responsiveness")] double? Responsiveness,
    [property: JsonPropertyName("skipped")] bool Skipped);

/// <summary>WS4 — the direction-aware scorer (Phase D).</summary>
public static class Scorer
{
    public static IReadOnlyList<CategoryScore> Score(
        IReadOnlyList<SampleRecord> records, IReadOnlyList<CategorySummary> summaries)
    {
        var scores = new List<CategoryScore>();
        foreach (var s in summaries)
        {
            if (s.Skipped)
            {
                scores.Add(new CategoryScore(s.Category, null, null, null, Skipped: true));
                continue;
            }

            if (s.Category == "pause_handling")
            {
                // TOR: fraction of (non-error) samples where the VAD ended the turn during the within-turn
                // pause — detected as more than one UserStopped edge. Lower is better; barging in on the gap
                // is the failure (a perfect score is "never spoke during the gap").
                var ok = records.Where(r => r.Category == s.Category && r.Error is null).ToList();
                double? tor = ok.Count == 0 ? null : (double)ok.Count(r => r.Signals.UserStoppedEdges > 1) / ok.Count;
                scores.Add(new CategoryScore(s.Category, tor, null, null, Skipped: false));
            }
            else
            {
                // smooth_turn_taking / user_interruption: faster, correctly-placed first word wins.
                scores.Add(new CategoryScore(s.Category, null, s.TtfbP50, Responsiveness(s.TtfbP50), Skipped: false));
            }
        }
        return scores;
    }

    /// <summary>Bounded inverse of the latency p50 → (0,1], higher is better; <c>null</c> when no latency.</summary>
    private static double? Responsiveness(double? ttfbP50Ms)
        => ttfbP50Ms is double ms ? Math.Round(1000.0 / (1000.0 + Math.Max(0, ms)), 4) : null;
}
