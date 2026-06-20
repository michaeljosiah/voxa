namespace Voxa.Audio.Diarization;

/// <summary>
/// The reference <see cref="IDiarizer"/> (VLS-005 WS1): composes an <see cref="ISpeakerSegmentation"/> and an
/// <see cref="ISpeakerEmbedding"/> and does everything else in pure C# — no model runtime is referenced, so
/// the clustering is testable on synthetic embeddings in the default lane.
///
/// <para>Four steps: (1) segment → absolute speech regions, merging overlaps from sliding windows and dropping
/// sub-<see cref="DiarizerConfig.MinSpeechDuration"/> blips; (2) embed each region; (3) constrained
/// agglomerative clustering by cosine distance (<see cref="SpeakerClustering"/>); (4) assign stable speaker
/// ids and merge consecutive same-speaker regions into contiguous <see cref="DiarizedSegment"/>s.</para>
/// </summary>
public sealed class DiarizationPipeline : IDiarizer
{
    private readonly ISpeakerSegmentation _segmentation;
    private readonly ISpeakerEmbedding _embedding;

    public DiarizationPipeline(ISpeakerSegmentation segmentation, ISpeakerEmbedding embedding)
    {
        _segmentation = segmentation ?? throw new ArgumentNullException(nameof(segmentation));
        _embedding = embedding ?? throw new ArgumentNullException(nameof(embedding));
    }

    public IReadOnlyList<DiarizedSegment> Diarize(ReadOnlySpan<float> audio, int sampleRate, DiarizerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate), "Sample rate must be positive.");

        // 1. Form disjoint, absolute-time speech regions from the model's (possibly overlapping) windows.
        var regions = FormRegions(_segmentation.Segment(audio, sampleRate), config.MinSpeechDuration);
        if (regions.Count == 0) return [];

        // 2. One embedding per region — slice the region's audio out of the full span.
        var embeddings = new List<float[]>(regions.Count);
        foreach (var region in regions)
        {
            var (offset, length) = ToSampleRange(region, sampleRate, audio.Length);
            embeddings.Add(_embedding.Embed(audio.Slice(offset, length), sampleRate));
        }

        // 3. Cluster the embeddings into stable speaker labels.
        var labels = SpeakerClustering.Cluster(
            embeddings, config.ClusteringThreshold, config.MinSpeakers, config.MaxSpeakers);

        // 4. Collapse consecutive same-speaker regions into contiguous segments.
        return MergeAdjacent(regions, labels);
    }

    // Flatten every window's regions, merge those that overlap or touch (sliding windows redundantly detect
    // the same speech), then drop anything shorter than minSpeechDuration. Output is sorted and disjoint.
    private static List<SpeechRegion> FormRegions(IReadOnlyList<SegmentationWindow> windows, double minSpeechDuration)
    {
        var flat = new List<SpeechRegion>();
        foreach (var window in windows)
            foreach (var region in window.Regions)
                if (region.End > region.Start)
                    flat.Add(region);

        flat.Sort((x, y) => x.Start.CompareTo(y.Start));

        var merged = new List<SpeechRegion>();
        foreach (var region in flat)
        {
            // Merge only true OVERLAPS (strict `<`) — those are the redundant detections sliding windows
            // produce for the same speech. Back-to-back regions that merely TOUCH (end == start) are kept
            // separate so clustering can still attribute them to different speakers; same-speaker neighbours
            // are recombined after clustering (step 4).
            if (merged.Count > 0 && region.Start < merged[^1].End)
            {
                if (region.End > merged[^1].End) // extend; never shrink on a fully-contained region
                    merged[^1] = merged[^1] with { End = region.End };
            }
            else
            {
                merged.Add(region);
            }
        }

        return merged.Where(r => r.End - r.Start >= minSpeechDuration).ToList();
    }

    // Map an absolute-time region to a [offset, length) sample range, clamped to the available audio.
    private static (int Offset, int Length) ToSampleRange(SpeechRegion region, int sampleRate, int audioLength)
    {
        int start = Math.Clamp((int)Math.Round(region.Start * sampleRate), 0, audioLength);
        int end = Math.Clamp((int)Math.Round(region.End * sampleRate), start, audioLength);
        return (start, end - start);
    }

    // Walk the (sorted) regions; while the next region shares this region's speaker label, absorb it.
    private static List<DiarizedSegment> MergeAdjacent(List<SpeechRegion> regions, int[] labels)
    {
        var segments = new List<DiarizedSegment>();
        for (int i = 0; i < regions.Count; i++)
        {
            int speaker = labels[i];
            double start = regions[i].Start;
            double end = regions[i].End;
            while (i + 1 < regions.Count && labels[i + 1] == speaker)
            {
                i++;
                end = regions[i].End;
            }
            segments.Add(new DiarizedSegment(start, end, speaker));
        }
        return segments;
    }
}
