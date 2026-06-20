namespace Voxa.Audio.Diarization;

/// <summary>
/// Constrained agglomerative clustering of speaker embeddings by cosine distance (VLS-005 WS1) — the
/// pure-vector-math core of <see cref="DiarizationPipeline"/>, with no model runtime and no I/O, so it is
/// exercised directly on synthetic embeddings in the default test lane.
///
/// <para><b>Centroid linkage:</b> a cluster's representative is the mean of its member vectors, and two
/// clusters' distance is the cosine distance between those means. This matches the linkage that speech-core /
/// pyannote calibrate their default <see cref="DiarizerConfig.ClusteringThreshold"/> (0.715) against — so the
/// borrowed threshold means what it does upstream. Clusters merge while the closest pair is below the
/// threshold; the count bounds (when &gt; 0) override the threshold to guarantee a floor / cap.</para>
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
    /// <remarks>
    /// If the bounds contradict (<c>0 &lt; maxSpeakers &lt; minSpeakers</c>) the floor wins — the loop stops at
    /// <paramref name="minSpeakers"/> clusters even though that exceeds the cap (an impossible request resolves
    /// to "at least minSpeakers" rather than throwing).
    /// </remarks>
    public static int[] Cluster(
        IReadOnlyList<float[]> embeddings, double threshold, int minSpeakers, int maxSpeakers)
    {
        ArgumentNullException.ThrowIfNull(embeddings);
        int n = embeddings.Count;
        if (n == 0) return [];
        if (n == 1) return [0];

        // Active clusters: member indices (for labels) + the running centroid (for the linkage distance).
        var members = new List<List<int>>(n);
        var centroids = new List<float[]>(n);
        for (int i = 0; i < n; i++)
        {
            members.Add([i]);
            centroids.Add(embeddings[i]); // read-only until merged, which replaces the slot with a fresh array
        }

        while (members.Count > 1)
        {
            // Never merge below the requested floor (this is checked first, so it wins a contradictory cap).
            if (minSpeakers > 0 && members.Count <= minSpeakers) break;

            var (a, b, best) = ClosestPair(centroids);

            // Past the threshold we stop — unless we still exceed the cap, in which case we must keep merging.
            bool overCap = maxSpeakers > 0 && members.Count > maxSpeakers;
            if (best >= threshold && !overCap) break;

            centroids[a] = WeightedMean(centroids[a], members[a].Count, centroids[b], members[b].Count);
            members[a].AddRange(members[b]);
            members.RemoveAt(b);
            centroids.RemoveAt(b);
        }

        return AssignStableLabels(members, n);
    }

    // The two clusters whose centroids are closest. Ties resolve to the lexicographically smallest (a, b)
    // because the scan is in index order and uses a strict "<", keeping merges deterministic.
    private static (int A, int B, double Distance) ClosestPair(List<float[]> centroids)
    {
        double best = double.MaxValue;
        int bestA = 0, bestB = 1;
        for (int a = 0; a < centroids.Count; a++)
        {
            for (int b = a + 1; b < centroids.Count; b++)
            {
                double d = CosineDistance(centroids[a], centroids[b]);
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

    // The mean of the two clusters' combined members, computed as the count-weighted mean of their centroids
    // (exact: centroid·count = the member sum, so this is the sum of all members over the total count).
    private static float[] WeightedMean(float[] a, int countA, float[] b, int countB)
    {
        var merged = new float[a.Length];
        int total = countA + countB;
        for (int k = 0; k < merged.Length; k++)
            merged[k] = (a[k] * countA + b[k] * countB) / total;
        return merged;
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
