namespace Voxa.Audio.Diarization;

/// <summary>
/// Constrained agglomerative clustering of speaker embeddings by cosine distance (VLS-005 WS1) — the
/// pure-vector-math core of <see cref="DiarizationPipeline"/>, with no model runtime and no I/O, so it is
/// exercised directly on synthetic embeddings in the default test lane. Average-linkage: clusters merge while
/// their mean pairwise cosine distance is below the threshold; the count bounds (when &gt; 0) override the
/// threshold to guarantee a floor / cap.
/// </summary>
internal static class SpeakerClustering
{
    /// <summary>
    /// Cluster <paramref name="embeddings"/> into 0-based speaker labels, one per embedding. Labels are stable
    /// by first appearance: the cluster containing the earliest embedding index is speaker 0, the next new
    /// cluster is 1, and so on — so the same input always yields the same labels.
    /// </summary>
    /// <param name="embeddings">One vector per speech region; all the same width (not required to be unit-norm).</param>
    /// <param name="threshold">Cosine-distance merge ceiling (<see cref="DiarizerConfig.ClusteringThreshold"/>).</param>
    /// <param name="minSpeakers">Lower bound on cluster count; never merge below it. <c>0</c> = no floor.</param>
    /// <param name="maxSpeakers">Upper bound on cluster count; keep merging past the threshold to reach it. <c>0</c> = no cap.</param>
    public static int[] Cluster(
        IReadOnlyList<float[]> embeddings, double threshold, int minSpeakers, int maxSpeakers)
    {
        ArgumentNullException.ThrowIfNull(embeddings);
        int n = embeddings.Count;
        if (n == 0) return [];
        if (n == 1) return [0];

        // Precompute the symmetric pairwise cosine-distance matrix once.
        var dist = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                dist[i, j] = dist[j, i] = CosineDistance(embeddings[i], embeddings[j]);

        // Active clusters as lists of original member indices; merge the closest pair each round.
        var clusters = new List<List<int>>(n);
        for (int i = 0; i < n; i++) clusters.Add([i]);

        while (clusters.Count > 1)
        {
            // Never merge below the requested floor.
            if (minSpeakers > 0 && clusters.Count <= minSpeakers) break;

            var (a, b, best) = ClosestPair(clusters, dist);

            // Past the threshold we stop — unless we still exceed the cap, in which case we must keep merging.
            bool overCap = maxSpeakers > 0 && clusters.Count > maxSpeakers;
            if (best >= threshold && !overCap) break;

            clusters[a].AddRange(clusters[b]);
            clusters.RemoveAt(b);
        }

        return AssignStableLabels(clusters, n);
    }

    // The two clusters with the smallest average-linkage distance. Ties resolve to the lexicographically
    // smallest (a, b) because the scan is in index order and uses a strict "<", keeping merges deterministic.
    private static (int A, int B, double Distance) ClosestPair(List<List<int>> clusters, double[,] dist)
    {
        double best = double.MaxValue;
        int bestA = 0, bestB = 1;
        for (int a = 0; a < clusters.Count; a++)
        {
            for (int b = a + 1; b < clusters.Count; b++)
            {
                double d = AverageLinkage(clusters[a], clusters[b], dist);
                if (d < best)
                {
                    best = d;
                    bestA = a;
                    bestB = b;
                }
            }
        }
        return (bestA, bestB, best);
    }

    private static double AverageLinkage(List<int> a, List<int> b, double[,] dist)
    {
        double sum = 0;
        foreach (int x in a)
            foreach (int y in b)
                sum += dist[x, y];
        return sum / (a.Count * b.Count);
    }

    // Label clusters 0..k-1 ordered by their earliest member index, then write each member's label.
    private static int[] AssignStableLabels(List<List<int>> clusters, int n)
    {
        var ordered = clusters.OrderBy(c => c.Min()).ToList();
        var labels = new int[n];
        for (int label = 0; label < ordered.Count; label++)
            foreach (int member in ordered[label])
                labels[member] = label;
        return labels;
    }

    /// <summary>
    /// Cosine distance in [0, 2] (1 − cosine similarity). A zero-magnitude vector has no direction, so it is
    /// treated as maximally dissimilar (2) rather than producing NaN — degenerate input never merges silently.
    /// </summary>
    internal static double CosineDistance(float[] a, float[] b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException($"Embedding dimensions differ ({a.Length} vs {b.Length}).");

        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            normA += (double)a[i] * a[i];
            normB += (double)b[i] * b[i];
        }
        if (normA <= 0 || normB <= 0) return 2.0;

        double similarity = dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
        // Clamp away floating-point overshoot past ±1 so distance stays in [0, 2].
        similarity = Math.Clamp(similarity, -1.0, 1.0);
        return 1.0 - similarity;
    }
}
