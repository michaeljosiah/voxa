using System.Globalization;
using System.Text;

namespace Voxa.TurnTaking;

/// <summary>
/// Per-category aggregate: counts + p50/p90/p99 of each timing — the same percentile shape
/// <c>bench/BASELINE.md</c> uses for the micro-benchmarks, so the two read alike. Failed samples are counted
/// but excluded from the percentiles.
/// </summary>
public sealed record CategorySummary(
    string Category, int N, int Errors,
    double? TtfbP50, double? TtfbP90, double? TtfbP99,
    double? SttP50, double? LlmP50, double? TtsP50, double? TotalWallP50,
    bool Skipped);

/// <summary>WS3 — roll the per-sample records up to per-category percentiles (VRT-001 §7, Phase C).</summary>
public static class Summarizer
{
    public static IReadOnlyList<CategorySummary> Summarize(
        IReadOnlyList<SampleRecord> records, IEnumerable<string>? skipped = null)
    {
        var summaries = new List<CategorySummary>();
        foreach (var category in CorpusWalker.CascadeFairCategories)
        {
            var inCat = records.Where(r => r.Category == category).ToList();
            if (inCat.Count == 0) continue;
            var ok = inCat.Where(r => r.Error is null).ToList();
            summaries.Add(new CategorySummary(
                category, inCat.Count, inCat.Count - ok.Count,
                Pct(ok, r => r.TimingsMs.TtftFirstAudioFromSpeechEnd, 50),
                Pct(ok, r => r.TimingsMs.TtftFirstAudioFromSpeechEnd, 90),
                Pct(ok, r => r.TimingsMs.TtftFirstAudioFromSpeechEnd, 99),
                Pct(ok, r => r.TimingsMs.Stt, 50),
                Pct(ok, r => r.TimingsMs.Llm, 50),
                Pct(ok, r => r.TimingsMs.Tts, 50),
                Pct(ok, r => r.TimingsMs.TotalWall, 50),
                Skipped: false));
        }

        // backchannel: discovered + skipped → a present-but-blank row, never scored (VRT-001 §5/§7).
        if (skipped?.Any(s => s.StartsWith(CorpusWalker.SkippedCategory, StringComparison.Ordinal)) == true)
            summaries.Add(new CategorySummary(
                CorpusWalker.SkippedCategory, 0, 0, null, null, null, null, null, null, null, Skipped: true));

        return summaries;
    }

    public static void WriteCsv(IReadOnlyList<CategorySummary> summaries, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("category,n,errors,ttfb_p50,ttfb_p90,ttfb_p99,stt_p50,llm_p50,tts_p50,total_wall_p50");
        foreach (var s in summaries)
            sb.AppendLine(string.Join(",",
                s.Category, s.N, s.Errors,
                F(s.TtfbP50), F(s.TtfbP90), F(s.TtfbP99),
                F(s.SttP50), F(s.LlmP50), F(s.TtsP50), F(s.TotalWallP50)));
        File.WriteAllText(path, sb.ToString());
    }

    private static double? Pct(IReadOnlyList<SampleRecord> records, Func<SampleRecord, double?> selector, double p)
    {
        var values = records.Select(selector).Where(v => v.HasValue).Select(v => v!.Value).OrderBy(v => v).ToList();
        return Percentile(values, p);
    }

    /// <summary>Linear-interpolation percentile (PERCENTILE.INC), <c>null</c> on empty.</summary>
    public static double? Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return null;
        if (sorted.Count == 1) return sorted[0];
        var rank = p / 100.0 * (sorted.Count - 1);
        var lo = (int)Math.Floor(rank);
        if (lo + 1 >= sorted.Count) return sorted[^1];
        return sorted[lo] + (rank - lo) * (sorted[lo + 1] - sorted[lo]);
    }

    private static string F(double? v) => v is double d ? d.ToString("0.###", CultureInfo.InvariantCulture) : "";
}
