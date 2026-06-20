namespace Voxa.Audio.Diarization;

/// <summary>
/// Diarization tunables (VLS-005 WS1), gathered into one record so the threshold — the one knob that
/// materially changes results — is exposed rather than buried. Defaults track speech-core's
/// <c>DiarizationPipeline</c>. The <see cref="DiarizationPipeline"/> applies <see cref="MinSpeechDuration"/>,
/// <see cref="ClusteringThreshold"/>, and the speaker-count bounds; <see cref="Onset"/> / <see cref="Offset"/>
/// tune the <i>segmentation model</i> instead (see their notes).
/// </summary>
public sealed record DiarizerConfig
{
    /// <summary>
    /// Speech-activity onset threshold for the segmentation <i>model</i>. The <see cref="ISpeakerSegmentation"/>
    /// seam takes no per-call config, so an <c>.Onnx</c> segmenter reads this at construction from the same
    /// <c>Voxa:Diarization</c> config section — it is not forwarded per <see cref="IDiarizer.Diarize"/> call.
    /// </summary>
    public double Onset { get; init; } = 0.5;

    /// <summary>Speech-activity offset threshold for the segmentation model — construction-time, like <see cref="Onset"/>.</summary>
    public double Offset { get; init; } = 0.3;

    /// <summary>Drop speech regions shorter than this many seconds (sub-threshold blips are noise, not turns).</summary>
    public double MinSpeechDuration { get; init; } = 0.3;

    /// <summary>
    /// Agglomerative merge ceiling on cosine distance: two clusters merge while their centroid-linkage distance
    /// is below this, and the merge stops once the closest pair is at least this far apart. speech-core's
    /// default is 0.715 (calibrated against centroid linkage, which <see cref="SpeakerClustering"/> uses) —
    /// lower splits speakers more eagerly, higher merges more.
    /// </summary>
    public double ClusteringThreshold { get; init; } = 0.715;

    /// <summary>Force <i>at least</i> this many speakers (clustering won't merge below it). <c>0</c> = auto (let the threshold decide).</summary>
    public int MinSpeakers { get; init; } = 0;

    /// <summary>Force <i>at most</i> this many speakers (clustering keeps merging past the threshold to honour it). <c>0</c> = auto (unbounded).</summary>
    public int MaxSpeakers { get; init; } = 0;
}
