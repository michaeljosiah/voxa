namespace Voxa.Audio.SileroVad;

/// <summary>
/// Tuning knobs for <see cref="SileroVadProcessor"/>. Defaults follow Pipecat's recipe — a
/// modest VAD confidence threshold combined with a low energy floor and time-based
/// start/stop windows. Browser-mic audio under aggressive AGC compresses dynamic range
/// and Silero v6's probabilities can hover lower than studio audio; both signals together
/// rejects keyboard / fan / chair noise without clipping real speech.
/// </summary>
public sealed record SileroVadOptions
{
    /// <summary>Sample rate of incoming audio. Silero v6 supports 16000 (512-sample windows) and 8000 (256-sample windows).</summary>
    public int SampleRate { get; init; } = 16000;

    /// <summary>
    /// Speech-probability threshold from the VAD model. Frame is "voiced" only when
    /// probability ≥ this value. Default 0.5 — Silero's standard. Pipecat uses 0.7
    /// for clean far-field; lower (0.3) for noisy / AGC'd browser mics.
    /// </summary>
    public float ConfidenceThreshold { get; init; } = 0.5f;

    /// <summary>
    /// Minimum normalized RMS to consider a window "voiced", combined with
    /// <see cref="ConfidenceThreshold"/> via AND. Drops pure silence and very low-energy
    /// noise even when the model returns spurious confidence. Default 0.003 = very lenient
    /// floor (well below typical speech RMS of 0.05+).
    /// </summary>
    public double MinRms { get; init; } = 0.003;

    /// <summary>
    /// Sustained voiced duration before the gate opens. Default 200 ms (Pipecat's
    /// <c>start_secs</c>). Lower for snappier triggers; higher to filter out brief sounds.
    /// </summary>
    public TimeSpan StartDuration { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Sustained unvoiced duration before the gate closes. Default 500 ms — slightly more
    /// lenient than Pipecat's 200 ms to avoid clipping the tail of utterances.
    /// </summary>
    public TimeSpan StopDuration { get; init; } = TimeSpan.FromMilliseconds(500);
}
