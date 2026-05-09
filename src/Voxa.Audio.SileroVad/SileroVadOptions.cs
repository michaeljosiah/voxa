namespace Voxa.Audio.SileroVad;

/// <summary>
/// Tuning knobs for <see cref="SileroVadProcessor"/>. Defaults are tuned for browser-mic input
/// with aggressive AGC + noise suppression — those compress dynamic range and pull speech
/// probabilities into the 0.3–0.6 range. For clean far-field mics, raise the activation
/// threshold to 0.5–0.7 to reduce false positives.
/// </summary>
public sealed record SileroVadOptions
{
    /// <summary>Sample rate of incoming audio. Silero v5 supports 16000 (512-sample windows) and 8000 (256-sample windows).</summary>
    public int SampleRate { get; init; } = 16000;

    /// <summary>
    /// Speech-probability threshold to OPEN the gate. Default 0.3 is lenient — works well for
    /// browser mics under AGC. For studio mics, try 0.5–0.7.
    /// </summary>
    public float ActivationThreshold { get; init; } = 0.3f;

    /// <summary>
    /// Speech-probability threshold to CLOSE the gate. Lower than <see cref="ActivationThreshold"/>
    /// for hysteresis — once we believe the user is speaking, stay in that state through brief dips.
    /// </summary>
    public float DeactivationThreshold { get; init; } = 0.2f;

    /// <summary>How many sustained silent windows before declaring speech-end. Default 8 windows × 32 ms = ~256 ms.</summary>
    public int MinSilenceWindows { get; init; } = 8;

    /// <summary>How many sustained speech windows before declaring speech-start. Default 1 — open immediately on first detection to minimise latency.</summary>
    public int MinSpeechWindows { get; init; } = 1;
}
