namespace Voxa.Processors;

/// <summary>
/// Arbitration knobs for background-result turns (VDX-008 §4.1) — how
/// <see cref="AgentLoopProcessor"/> schedules a <see cref="Frames.BackgroundTaskCompletedFrame"/>
/// against live conversation. Defaults are safe for voice: hold while the user speaks, release
/// data-ordered behind their utterance's turn.
/// </summary>
public sealed record BackgroundResultOptions
{
    /// <summary>
    /// Hold completed results while the user is speaking, releasing them behind the utterance's
    /// final transcription (or after <see cref="HeldResultReleaseTimeout"/> if no final arrives).
    /// Off ⇒ results enqueue immediately; the FIFO turn worker still serializes actual turns.
    /// </summary>
    public bool HoldWhileUserSpeaking { get; init; } = true;

    /// <summary>Bound on held results, drop-oldest (the repo's backpressure convention). A dropped
    /// result raises a diagnostics event; it is not an error.</summary>
    public int MaxPendingResults { get; init; } = 4;

    /// <summary>
    /// Fallback release after the user stops speaking when no final transcription ever arrives
    /// (the release signal is data-ordered on the final, not the stop-speaking edge — the STT
    /// stage forwards stop-speaking before the transcript, and system frames can overtake data
    /// frames cross-channel).
    /// </summary>
    public TimeSpan HeldResultReleaseTimeout { get; init; } = TimeSpan.FromSeconds(2);
}
