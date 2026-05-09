namespace Voxa.Audio.SileroVad;

/// <summary>
/// Tuning knobs for <see cref="SileroVadProcessor"/>. Defaults match what the Silero project
/// recommends for typical voice agents.
/// </summary>
public sealed record SileroVadOptions
{
    /// <summary>Sample rate of incoming audio. Silero v5 supports 16000 (512-sample windows) and 8000 (256-sample windows).</summary>
    public int SampleRate { get; init; } = 16000;

    /// <summary>Speech-probability threshold to OPEN the gate (transition silence → speaking).</summary>
    public float ActivationThreshold { get; init; } = 0.5f;

    /// <summary>
    /// Speech-probability threshold to CLOSE the gate (transition speaking → silence). Lower than
    /// <see cref="ActivationThreshold"/> to give hysteresis — once we believe the user is speaking,
    /// we stay in that state through brief drops.
    /// </summary>
    public float DeactivationThreshold { get; init; } = 0.35f;

    /// <summary>How many sustained silent windows before declaring speech-end. Default 8 windows × 32 ms = ~256 ms.</summary>
    public int MinSilenceWindows { get; init; } = 8;

    /// <summary>How many sustained speech windows before declaring speech-start. Default 2 = ~64 ms — keeps latency low.</summary>
    public int MinSpeechWindows { get; init; } = 2;
}
