namespace Voxa.Studio.Services;

/// <summary>How a hypothesis token relates to the reference in the WER alignment.</summary>
public enum WerOp { Match, Substitution, Insertion, Deletion }

/// <summary>
/// One aligned token for the diff display. <see cref="Word"/> is the hypothesis word (or the
/// reference word for a deletion — the thing that went missing); <see cref="Reference"/> carries
/// what a substitution replaced.
/// </summary>
public sealed record WerToken(string Word, WerOp Op, string? Reference = null)
{
    public bool IsMatch => Op == WerOp.Match;
    public bool IsSubstitution => Op == WerOp.Substitution;
    public bool IsInsertion => Op == WerOp.Insertion;
    public bool IsDeletion => Op == WerOp.Deletion;
    /// <summary>Diff tooltip: what the reference had at this position.</summary>
    public string Detail => Op switch
    {
        WerOp.Substitution => $"was \"{Reference}\"",
        WerOp.Insertion => "inserted",
        WerOp.Deletion => "deleted",
        _ => string.Empty,
    };
}

/// <summary>The §6.1 accuracy harness result: WER plus the alignment that produced it.</summary>
public sealed record WerResult(
    double Wer, int Substitutions, int Insertions, int Deletions, int ReferenceWords,
    IReadOnlyList<WerToken> Tokens)
{
    public string WerText => ReferenceWords == 0 ? "—" : $"{Wer * 100:F1}";
    public string CountsText => $"{Substitutions} sub · {Insertions} ins · {Deletions} del";
}

/// <summary>
/// Word error rate via Levenshtein alignment on normalized word tokens (brief §6: "a ~80-line
/// Levenshtein"). Normalization lowercases and strips punctuation so "Dog." matches "dog" —
/// WER should measure recognition, not orthography.
/// </summary>
public static class WordErrorRate
{
    public static WerResult Compute(string reference, string hypothesis)
    {
        var refWords = Tokenize(reference);
        var hypWords = Tokenize(hypothesis);

        // Classic DP edit-distance table over words, with a backtrace for the diff.
        int n = refWords.Length, m = hypWords.Length;
        var cost = new int[n + 1, m + 1];
        for (int i = 0; i <= n; i++) cost[i, 0] = i;
        for (int j = 0; j <= m; j++) cost[0, j] = j;
        for (int i = 1; i <= n; i++)
        for (int j = 1; j <= m; j++)
        {
            int sub = cost[i - 1, j - 1] + (refWords[i - 1].Norm == hypWords[j - 1].Norm ? 0 : 1);
            cost[i, j] = Math.Min(sub, Math.Min(cost[i - 1, j] + 1, cost[i, j - 1] + 1));
        }

        var tokens = new List<WerToken>(Math.Max(n, m));
        int s = 0, ins = 0, del = 0;
        for (int i = n, j = m; i > 0 || j > 0;)
        {
            if (i > 0 && j > 0 && cost[i, j] == cost[i - 1, j - 1]
                && refWords[i - 1].Norm == hypWords[j - 1].Norm)
            {
                tokens.Add(new WerToken(hypWords[j - 1].Raw, WerOp.Match));
                i--; j--;
            }
            else if (i > 0 && j > 0 && cost[i, j] == cost[i - 1, j - 1] + 1)
            {
                tokens.Add(new WerToken(hypWords[j - 1].Raw, WerOp.Substitution, refWords[i - 1].Raw));
                s++; i--; j--;
            }
            else if (j > 0 && cost[i, j] == cost[i, j - 1] + 1)
            {
                tokens.Add(new WerToken(hypWords[j - 1].Raw, WerOp.Insertion));
                ins++; j--;
            }
            else
            {
                tokens.Add(new WerToken(refWords[i - 1].Raw, WerOp.Deletion));
                del++; i--;
            }
        }
        tokens.Reverse();

        var wer = n == 0 ? (m == 0 ? 0 : 1) : (s + ins + del) / (double)n;
        return new WerResult(wer, s, ins, del, n, tokens);
    }

    private static (string Raw, string Norm)[] Tokenize(string text) =>
        (text ?? string.Empty)
        .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Select(w => (Raw: w, Norm: Normalize(w)))
        .Where(w => w.Norm.Length > 0)
        .ToArray();

    private static string Normalize(string word) =>
        new(word.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
