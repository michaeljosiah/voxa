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
    /// Sustained unvoiced duration before the gate closes — also the "end-of-turn" timeout for
    /// downstream STT flush. Default 800 ms (matches Pipecat's <c>stop_secs=0.8</c>). Raise to
    /// 1200–1500 ms for slow speakers or anyone who pauses mid-sentence to think; lower to
    /// 400–500 ms for crisp speakers and snappier turn-taking. There's no perfect value —
    /// silence detection alone can't tell a within-sentence breath from end-of-turn. A future
    /// LLM-based smart turn analyzer is the proper fix.
    /// </summary>
    public TimeSpan StopDuration { get; init; } = TimeSpan.FromMilliseconds(800);

    /// <summary>
    /// Audio kept in a rolling buffer that's prepended to the gated stream when speech is
    /// detected — so the first word's onset isn't lost while the gate is making up its mind.
    /// Default 300 ms (matches Pipecat's <c>speech_pad_ms</c>). Must be at least
    /// <see cref="StartDuration"/> for the rule "fire StartedSpeaking with the audio that
    /// triggered it" to hold.
    /// </summary>
    public TimeSpan PrerollDuration { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Optional turn-end confirmation (smart-turn seam). When set, the gate reaching its silence
    /// timeout (<see cref="StopDuration"/>) does NOT immediately emit
    /// <c>UserStoppedSpeakingFrame</c> — this callback decides. Return <c>true</c> to confirm the
    /// turn is over (emit the frame); return <c>false</c> to treat it as a mid-sentence pause
    /// (keep the gate open and re-evaluate after another <see cref="StopDuration"/> of silence).
    /// This is what lets <see cref="StopDuration"/> be aggressive (e.g. 200 ms) without cutting
    /// off speakers who pause to think — a smart-turn classifier plugs in here.
    ///
    /// <para>
    /// The <see cref="ReadOnlyMemory{T}"/> is a snapshot of the most recent speech audio leading up
    /// to the silence (up to ~1 s, 16-bit PCM at <see cref="SampleRate"/>). Default <c>null</c>
    /// (classic silence-only behavior — byte-for-byte unchanged).
    /// </para>
    /// </summary>
    public Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<bool>>? ConfirmTurnEnd { get; init; }
}
