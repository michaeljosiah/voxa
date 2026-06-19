using System.Text.Json;

namespace Voxa.TurnTaking;

/// <summary>One discovered sample: its category, id, input WAV, and optional reference transcript.</summary>
public sealed record CorpusSample(string Category, string SampleId, string InputWavPath, string? ReferenceText);

/// <summary>
/// Walks the Full-Duplex-Bench layout (<c>&lt;corpus&gt;/&lt;category&gt;/&lt;sample-id&gt;/…</c>). Enumerates the three
/// cascade-fair categories; the fourth (<c>backchannel</c>) is discovered and logged <c>skipped</c>, never
/// run — it needs a full-duplex model a cascade structurally can't be (VRT-001 §5).
/// </summary>
public static class CorpusWalker
{
    public static readonly IReadOnlyList<string> CascadeFairCategories =
        ["pause_handling", "smooth_turn_taking", "user_interruption"];

    public const string SkippedCategory = "backchannel";

    public static IReadOnlyList<CorpusSample> Walk(
        string corpusDir, string? onlyCategory, int? limit, Action<string>? onSkip = null)
    {
        var samples = new List<CorpusSample>();
        if (!Directory.Exists(corpusDir)) return samples;

        // Discover the skipped category if present and log it once — never enumerate its samples.
        if (Directory.Exists(Path.Combine(corpusDir, SkippedCategory)))
            onSkip?.Invoke($"{SkippedCategory}: skipped — N/A for a half-duplex cascade (VRT-001 §5)");

        foreach (var category in CascadeFairCategories)
        {
            if (onlyCategory is not null && !string.Equals(onlyCategory, category, StringComparison.OrdinalIgnoreCase))
                continue;
            var categoryDir = Path.Combine(corpusDir, category);
            if (!Directory.Exists(categoryDir)) continue;

            var taken = 0;
            foreach (var sampleDir in Directory.EnumerateDirectories(categoryDir).OrderBy(d => d, StringComparer.Ordinal))
            {
                if (limit is int cap && taken >= cap) break;
                var wav = FindInputWav(sampleDir);
                if (wav is null) continue;
                samples.Add(new CorpusSample(category, Path.GetFileName(sampleDir), wav, ReadReference(sampleDir)));
                taken++;
            }
        }
        return samples;
    }

    private static string? FindInputWav(string sampleDir)
    {
        var preferred = Path.Combine(sampleDir, "input.wav");
        if (File.Exists(preferred)) return preferred;
        return Directory.EnumerateFiles(sampleDir, "*.wav").OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault();
    }

    private static string? ReadReference(string sampleDir)
    {
        var meta = Path.Combine(sampleDir, "meta.json");
        if (!File.Exists(meta)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(meta));
            return doc.RootElement.TryGetProperty("reference", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString()
                : null;
        }
        catch { return null; }
    }
}
