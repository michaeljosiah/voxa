namespace Voxa.Audio.Diarization.Tests;

/// <summary>
/// Direct tests of the pure clustering core on synthetic embeddings — the default-lane surface the spec calls
/// for (no models, fully deterministic). Unit vectors make cosine distances exact: identical → 0, orthogonal
/// → 1, opposite → 2.
/// </summary>
public class SpeakerClusteringTests
{
    private static readonly float[] A = [1f, 0f]; // direction "speaker A"
    private static readonly float[] B = [0f, 1f]; // direction "speaker B" (orthogonal to A → distance 1.0)

    [Fact]
    public void Empty_input_yields_no_labels()
        => Assert.Empty(SpeakerClustering.Cluster([], 0.715, 0, 0));

    [Fact]
    public void Single_embedding_is_speaker_zero()
        => Assert.Equal([0], SpeakerClustering.Cluster([A], 0.715, 0, 0));

    [Fact]
    public void Tight_clusters_become_distinct_speakers()
    {
        // Three groups of near-identical vectors → three speakers, grouped correctly.
        float[][] embeddings =
        [
            [1f, 0f, 0f], [0.99f, 0.01f, 0f],   // speaker 0
            [0f, 1f, 0f], [0.01f, 0.99f, 0f],   // speaker 1
            [0f, 0f, 1f],                        // speaker 2
        ];
        var labels = SpeakerClustering.Cluster(embeddings, 0.715, 0, 0);

        Assert.Equal(3, labels.Distinct().Count());
        Assert.Equal(labels[0], labels[1]); // the two near-identical pairs share a speaker
        Assert.Equal(labels[2], labels[3]);
        Assert.NotEqual(labels[0], labels[2]);
        Assert.NotEqual(labels[0], labels[4]);
    }

    [Theory]
    [InlineData(0.0, 3)]  // never merge (distance is never < 0) → every embedding is its own speaker
    [InlineData(0.5, 2)]  // A≡C merge (distance 0); A⊥B stays apart (distance 1.0 ≥ 0.5)
    [InlineData(1.5, 1)]  // everything within 1.5 → one speaker
    public void Threshold_controls_speaker_count(double threshold, int expectedSpeakers)
    {
        float[][] embeddings = [A, B, A]; // distances: A-B=1, A-A=0
        var labels = SpeakerClustering.Cluster(embeddings, threshold, 0, 0);
        Assert.Equal(expectedSpeakers, labels.Distinct().Count());
    }

    [Fact]
    public void MaxSpeakers_caps_the_count_even_past_the_threshold()
    {
        // Threshold 0 alone → 3 speakers; the cap forces merging down to 2 (closest pair, A≡C, merges first).
        float[][] embeddings = [A, B, A];
        var labels = SpeakerClustering.Cluster(embeddings, 0.0, minSpeakers: 0, maxSpeakers: 2);
        Assert.Equal(2, labels.Distinct().Count());
        Assert.Equal(labels[0], labels[2]); // the two A's were the closest pair
        Assert.NotEqual(labels[0], labels[1]);
    }

    [Fact]
    public void MinSpeakers_floors_the_count_even_below_the_threshold()
    {
        // Threshold 1.5 alone → 1 speaker; the floor stops merging at 2.
        float[][] embeddings = [A, B, A];
        var labels = SpeakerClustering.Cluster(embeddings, 1.5, minSpeakers: 2, maxSpeakers: 0);
        Assert.Equal(2, labels.Distinct().Count());
    }

    [Fact]
    public void Labels_are_stable_by_first_appearance()
    {
        // Index 0 is the lone B; indices 1,2 are the A pair. Speaker 0 must be the EARLIEST index, not the
        // largest cluster.
        float[][] embeddings = [B, A, A];
        var labels = SpeakerClustering.Cluster(embeddings, 0.715, 0, 0);
        Assert.Equal([0, 1, 1], labels);
    }

    [Fact]
    public void Same_input_is_deterministic()
    {
        float[][] embeddings = [A, B, A, B, [0.5f, 0.5f]];
        var first = SpeakerClustering.Cluster(embeddings, 0.715, 0, 0);
        var second = SpeakerClustering.Cluster(embeddings, 0.715, 0, 0);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Tied_distances_merge_the_lexicographically_smallest_pair()
    {
        // Three orthogonal one-hot vectors → every pairwise distance is EXACTLY 1.0 (dot 0, norm 1; no float
        // rounding). A forced merge (cap 2) must pick the first pair (indices 0,1) by the deterministic
        // tie-break, not an arbitrary one.
        float[][] embeddings = [[1f, 0f, 0f], [0f, 1f, 0f], [0f, 0f, 1f]];
        var labels = SpeakerClustering.Cluster(embeddings, threshold: 0.0, minSpeakers: 0, maxSpeakers: 2);
        Assert.Equal([0, 0, 1], labels);
    }

    [Theory]
    [InlineData(new[] { 1f, 0f }, new[] { 1f, 0f }, 0.0)]    // identical
    [InlineData(new[] { 1f, 0f }, new[] { 0f, 1f }, 1.0)]    // orthogonal
    [InlineData(new[] { 1f, 0f }, new[] { -1f, 0f }, 2.0)]   // opposite
    [InlineData(new[] { 2f, 0f }, new[] { 1f, 0f }, 0.0)]    // same direction, different magnitude
    [InlineData(new[] { 0f, 0f }, new[] { 1f, 0f }, 2.0)]    // zero vector → max distance, not NaN
    public void Cosine_distance_is_correct(float[] a, float[] b, double expected)
        => Assert.Equal(expected, SpeakerClustering.CosineDistance(a, b), precision: 5);

    [Fact]
    public void Mismatched_embedding_dimensions_throw()
        => Assert.Throws<ArgumentException>(() => SpeakerClustering.CosineDistance([1f, 0f], [1f, 0f, 0f]));
}
