namespace Voxa.Audio.Diarization.Tests;

/// <summary>
/// End-to-end pipeline tests with in-memory fake seams — no models. The fake embedder turns each region's
/// audio "marker" (the constant value filling it) into a distinct one-hot direction, so cosine distance is 0
/// within a speaker and 1 across speakers, exercising the real region-forming, clustering, and merge logic.
/// </summary>
public class DiarizationPipelineTests
{
    private const int SampleRate = 16000;

    [Fact]
    public void Alternating_speakers_get_distinct_labels()
    {
        var audio = BuildAudio((1, 0.0, 1.0), (2, 1.0, 2.0), (1, 2.0, 3.0));
        var pipeline = Pipeline(Window(3.0, R(0, 1), R(1, 2), R(2, 3)));

        var result = pipeline.Diarize(audio, SampleRate, new DiarizerConfig());

        Assert.Equal(3, result.Count);
        Assert.Equal([0, 1, 0], result.Select(s => s.Speaker).ToArray());
        Assert.Equal(2, result.Select(s => s.Speaker).Distinct().Count());
        Assert.Equal(0.0, result[0].Start, 3);
        Assert.Equal(3.0, result[^1].End, 3);
    }

    [Fact]
    public void Consecutive_same_speaker_regions_merge_into_one_segment()
    {
        var audio = BuildAudio((1, 0.0, 1.0), (1, 1.0, 2.0), (2, 2.0, 3.0));
        var pipeline = Pipeline(Window(3.0, R(0, 1), R(1, 2), R(2, 3)));

        var result = pipeline.Diarize(audio, SampleRate, new DiarizerConfig());

        Assert.Equal(2, result.Count);
        Assert.Equal(0.0, result[0].Start, 3);
        Assert.Equal(2.0, result[0].End, 3);   // the two marker-1 regions collapsed
        Assert.Equal(2.0, result[1].Start, 3);
        Assert.NotEqual(result[0].Speaker, result[1].Speaker);
    }

    [Fact]
    public void Regions_shorter_than_min_speech_duration_are_dropped()
    {
        // A 0.1 s marker-2 blip between two marker-1 turns, isolated by gaps so it isn't overlap-merged.
        var audio = BuildAudio((1, 0.0, 1.0), (2, 1.5, 1.6), (1, 2.0, 3.0));
        var pipeline = Pipeline(Window(3.0, R(0, 1.0), R(1.5, 1.6), R(2.0, 3.0)));

        var result = pipeline.Diarize(audio, SampleRate, new DiarizerConfig()); // MinSpeechDuration 0.3

        Assert.Single(result.Select(s => s.Speaker).Distinct()); // the blip never became a speaker
        Assert.All(result, s => Assert.Equal(0, s.Speaker));
    }

    [Fact]
    public void Overlapping_window_regions_are_deduplicated()
    {
        // Two sliding windows redundantly detect the same marker-1 speech as overlapping regions.
        var audio = BuildAudio((1, 0.0, 3.0));
        var pipeline = Pipeline(Window(2.0, R(0, 2)), Window(3.0, R(1, 3)));

        var result = pipeline.Diarize(audio, SampleRate, new DiarizerConfig());

        Assert.Single(result);
        Assert.Equal(0.0, result[0].Start, 3);
        Assert.Equal(3.0, result[0].End, 3);
    }

    [Fact]
    public void No_speech_regions_yields_no_segments()
    {
        var audio = BuildAudio((1, 0.0, 1.0));
        var pipeline = Pipeline(Window(1.0)); // a window with no regions
        Assert.Empty(pipeline.Diarize(audio, SampleRate, new DiarizerConfig()));
    }

    [Fact]
    public void Max_speakers_caps_the_diarized_count()
    {
        // Three distinct markers → three speakers by default; MaxSpeakers=2 forces two.
        var audio = BuildAudio((1, 0.0, 1.0), (2, 1.0, 2.0), (3, 2.0, 3.0));
        var pipeline = Pipeline(Window(3.0, R(0, 1), R(1, 2), R(2, 3)));

        var unbounded = pipeline.Diarize(audio, SampleRate, new DiarizerConfig());
        var capped = pipeline.Diarize(audio, SampleRate, new DiarizerConfig { MaxSpeakers = 2 });

        Assert.Equal(3, unbounded.Select(s => s.Speaker).Distinct().Count());
        Assert.Equal(2, capped.Select(s => s.Speaker).Distinct().Count());
    }

    [Fact]
    public void Same_input_is_deterministic()
    {
        var audio = BuildAudio((1, 0.0, 1.0), (2, 1.0, 2.0), (1, 2.0, 3.0));
        var pipeline = Pipeline(Window(3.0, R(0, 1), R(1, 2), R(2, 3)));

        var a = pipeline.Diarize(audio, SampleRate, new DiarizerConfig());
        var b = pipeline.Diarize(audio, SampleRate, new DiarizerConfig());
        Assert.Equal(a, b); // records → structural equality
    }

    [Fact]
    public void Constructor_rejects_null_seams()
    {
        var embed = new MarkerEmbedding(4);
        var seg = new FakeSegmentation();
        Assert.Throws<ArgumentNullException>(() => new DiarizationPipeline(null!, embed));
        Assert.Throws<ArgumentNullException>(() => new DiarizationPipeline(seg, null!));
    }

    [Fact]
    public void Diarize_validates_its_arguments()
    {
        var pipeline = Pipeline(Window(1.0, R(0, 1)));
        var audio = BuildAudio((1, 0.0, 1.0));
        Assert.Throws<ArgumentNullException>(() => pipeline.Diarize(audio, SampleRate, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => pipeline.Diarize(audio, 0, new DiarizerConfig()));
    }

    [Fact]
    public void Min_speakers_floors_the_diarized_count()
    {
        // Two identical-marker regions would cluster to one speaker; MinSpeakers=2 forces two.
        var audio = BuildAudio((1, 0.0, 1.0), (1, 1.0, 2.0));
        var pipeline = Pipeline(Window(2.0, R(0, 1), R(1, 2)));

        var result = pipeline.Diarize(audio, SampleRate, new DiarizerConfig { MinSpeakers = 2 });

        Assert.Equal(2, result.Select(s => s.Speaker).Distinct().Count());
    }

    [Fact]
    public void Same_speaker_regions_across_a_gap_merge_into_one_turn()
    {
        // [0,1] and [3,4] are one speaker with 2 s of silence between (no region there). The documented turn
        // semantics collapse them into a single gap-spanning segment.
        var audio = BuildAudio((1, 0.0, 1.0), (1, 3.0, 4.0));
        var pipeline = Pipeline(Window(4.0, R(0, 1), R(3, 4)));

        var result = pipeline.Diarize(audio, SampleRate, new DiarizerConfig());

        Assert.Single(result);
        Assert.Equal(0.0, result[0].Start, 3);
        Assert.Equal(4.0, result[0].End, 3);
    }

    [Fact]
    public void Regions_past_the_audio_buffer_are_dropped()
    {
        // The second region lands entirely past the 1 s of audio → zero-length slice → dropped, not embedded.
        var audio = BuildAudio((1, 0.0, 1.0));
        var pipeline = Pipeline(Window(6.0, R(0, 1), R(5, 6)));

        var result = pipeline.Diarize(audio, SampleRate, new DiarizerConfig());

        Assert.Single(result);
        Assert.Equal(0.0, result[0].Start, 3);
        Assert.Equal(1.0, result[0].End, 3);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static DiarizationPipeline Pipeline(params SegmentationWindow[] windows)
        => new(new FakeSegmentation(windows), new MarkerEmbedding(dim: 8));

    private static SegmentationWindow Window(double end, params SpeechRegion[] regions)
        => new(0.0, end, regions);

    private static SpeechRegion R(double start, double end) => new(start, end);

    // Audio where each turn's sample range is filled with its integer "marker", so the embedder can recover it.
    private static float[] BuildAudio(params (int Marker, double Start, double End)[] turns)
    {
        double maxEnd = turns.Max(t => t.End);
        var audio = new float[(int)Math.Ceiling(maxEnd * SampleRate)];
        foreach (var (marker, start, end) in turns)
        {
            int s = Math.Clamp((int)Math.Round(start * SampleRate), 0, audio.Length);
            int e = Math.Clamp((int)Math.Round(end * SampleRate), s, audio.Length);
            for (int i = s; i < e; i++) audio[i] = marker;
        }
        return audio;
    }

    private sealed class FakeSegmentation(params SegmentationWindow[] windows) : ISpeakerSegmentation
    {
        public IReadOnlyList<SegmentationWindow> Segment(ReadOnlySpan<float> audio, int sampleRate) => windows;
    }

    // Maps a region's marker (read from the middle of its audio slice) to a one-hot direction, so distinct
    // markers are orthogonal (cosine distance 1) and equal markers are identical (distance 0).
    private sealed class MarkerEmbedding(int dim) : ISpeakerEmbedding
    {
        public int EmbeddingDim => dim;

        public float[] Embed(ReadOnlySpan<float> audio, int sampleRate)
        {
            int marker = audio.Length == 0 ? 0 : (int)Math.Round(audio[audio.Length / 2]);
            var v = new float[dim];
            if (marker >= 1 && marker <= dim) v[marker - 1] = 1f;
            return v;
        }
    }
}
