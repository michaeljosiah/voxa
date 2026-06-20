namespace Voxa.Audio.Diarization;

/// <summary>
/// Per-window speech activity over a whole recording (VLS-005 WS1) — mirrors speech-core's
/// <c>SegmentationInterface</c>. Implementations are model-backed (e.g. Pyannote) and live in the opt-in
/// <c>Voxa.Audio.Diarization.Onnx</c> package; the pure-C# <see cref="DiarizationPipeline"/> consumes this
/// seam without referencing any model runtime. Audio is mono, normalized float samples at the given sample rate.
/// </summary>
public interface ISpeakerSegmentation
{
    /// <summary>Detect speech regions across <paramref name="audio"/>, grouped by the model's processing windows.</summary>
    IReadOnlyList<SegmentationWindow> Segment(ReadOnlySpan<float> audio, int sampleRate);
}

/// <summary>
/// One segmentation window's speech activity: the window's absolute time span plus the speech regions it
/// found. <see cref="SpeechRegion"/> times are <b>absolute</b> (seconds from the start of the recording), so
/// the pipeline can flatten windows without tracking per-window offsets — a model that works in local-window
/// coordinates maps them to absolute time before returning.
/// </summary>
public sealed record SegmentationWindow(double Start, double End, IReadOnlyList<SpeechRegion> Regions);

/// <summary>A single speech region, in <b>absolute</b> seconds from the start of the recording.</summary>
public sealed record SpeechRegion(double Start, double End);

/// <summary>
/// A fixed-dimension speaker embedding for a span of audio (VLS-005 WS1) — mirrors speech-core's
/// <c>EmbeddingInterface</c>. Model-backed (e.g. WeSpeaker); the <see cref="DiarizationPipeline"/> calls it
/// once per speech region and clusters the results by cosine distance. The vector need not be unit-norm — the
/// clustering normalizes via cosine distance.
/// </summary>
public interface ISpeakerEmbedding
{
    /// <summary>The fixed width of every embedding this model produces.</summary>
    int EmbeddingDim { get; }

    /// <summary>Embed one span of mono float audio at <paramref name="sampleRate"/> into a speaker vector.</summary>
    float[] Embed(ReadOnlySpan<float> audio, int sampleRate);
}

/// <summary>
/// The orchestrating seam (VLS-005 WS1): audio in, speaker-attributed segments out. The pure-C#
/// <see cref="DiarizationPipeline"/> is the reference implementation; the interface lets a host swap in a
/// fully model-side diarizer later without changing consumers.
/// </summary>
public interface IDiarizer
{
    /// <summary>Diarize a whole recording into contiguous, speaker-labelled segments.</summary>
    IReadOnlyList<DiarizedSegment> Diarize(ReadOnlySpan<float> audio, int sampleRate, DiarizerConfig config);
}

/// <summary>
/// A contiguous span attributed to one clustered speaker. <see cref="Speaker"/> is a 0-based id, stable per
/// run (the earliest-starting speaker is 0) but arbitrary across recordings — diarization answers "same or
/// different speaker", not "which named person" (that is speaker identification, a follow-up).
/// </summary>
public sealed record DiarizedSegment(double Start, double End, int Speaker);
