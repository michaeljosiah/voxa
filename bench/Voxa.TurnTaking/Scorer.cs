using System.Text.Json.Serialization;

namespace Voxa.TurnTaking;

/// <summary>
/// Per-category direction-aware score (VRT-001 §7).
/// <list type="bullet">
/// <item><c>pause_handling</c> → a turn-offset-rate (lower is better: silence through a thinking-pause is the win).</item>
/// <item><c>smooth_turn_taking</c> → first-word latency (the ttfb p50) + a bounded responsiveness.</item>
/// <item><c>user_interruption</c> → barge-in YIELD latency (how fast the bot stops when interrupted) + responsiveness.
///   NOT reply latency — a system that talks over the interruption but answers quickly must not score well.</item>
/// </list>
/// </summary>
public sealed record CategoryScore(
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("tor")] double? Tor,
    [property: JsonPropertyName("ttfb_p50_ms")] double? TtfbP50Ms,
    [property: JsonPropertyName("barge_in_yield_p50_ms")] double? BargeInYieldP50Ms,
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
                scores.Add(new CategoryScore(s.Category, null, null, null, null, Skipped: true));
                continue;
            }

            var ok = records.Where(r => r.Category == s.Category && r.Error is null).ToList();
            switch (s.Category)
            {
                case "pause_handling":
                    // TOR: fraction of samples where the VAD ended the turn during the within-turn pause —
                    // detected as more than one UserStopped edge. Lower is better (barging in on the gap is the
                    // failure; a perfect score is "never spoke during the gap").
                    double? tor = ok.Count == 0 ? null : (double)ok.Count(r => r.Signals.UserStoppedEdges > 1) / ok.Count;
                    scores.Add(new CategoryScore(s.Category, tor, null, null, null, Skipped: false));
                    break;

                case "user_interruption":
                    // Barge-in YIELD (UserStarted-while-bot-speaking → BotStopped/Interrupted), not reply latency.
                    // Null when no barge-in was exercised (the offline file-driven harness produces none).
                    var yield = Summarizer.Percentile(
                        ok.Select(r => r.TimingsMs.BargeInYieldMs).Where(v => v.HasValue).Select(v => v!.Value).OrderBy(v => v).ToList(), 50);
                    scores.Add(new CategoryScore(s.Category, null, null, yield, Responsiveness(yield), Skipped: false));
                    break;

                default: // smooth_turn_taking — faster, correctly-placed first word wins.
                    scores.Add(new CategoryScore(s.Category, null, s.TtfbP50, null, Responsiveness(s.TtfbP50), Skipped: false));
                    break;
            }
        }
        return scores;
    }

    /// <summary>Bounded inverse of a latency p50 → (0,1], higher is better; <c>null</c> when no latency.</summary>
    private static double? Responsiveness(double? latencyMs)
        => latencyMs is double ms ? Math.Round(1000.0 / (1000.0 + Math.Max(0, ms)), 4) : null;
}
