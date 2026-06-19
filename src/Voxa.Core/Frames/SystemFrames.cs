namespace Voxa.Frames;

/// <summary>
/// User started speaking while the bot was speaking — flush the in-flight downstream stream.
/// Cancels the current data frame's <see cref="CancellationToken"/> in every processor.
/// </summary>
public sealed record InterruptionFrame : SystemFrame;

/// <summary>Bot started producing audio. Useful for client-side UI (waveform, "speaking" indicator).</summary>
public sealed record BotStartedSpeakingFrame : SystemFrame;

/// <summary>Bot finished its turn.</summary>
public sealed record BotStoppedSpeakingFrame : SystemFrame;

/// <summary>VAD/turn-taker confirmed the user started speaking.</summary>
public sealed record UserStartedSpeakingFrame : SystemFrame;

/// <summary>VAD/turn-taker confirmed the user stopped speaking.</summary>
public sealed record UserStoppedSpeakingFrame : SystemFrame;

/// <summary>
/// Eager/speculative end-of-utterance marker (VRT-002 WS1). Emitted by the VAD when unvoiced silence reaches
/// <c>EagerSttDelay</c> (which is strictly &lt; <c>StopDuration</c>): it tells <c>SpeechToTextProcessor</c> to
/// flush the buffered utterance speculatively for <see cref="UtteranceId"/> so transcription overlaps the rest
/// of the hangover, and tells the sink that a following <see cref="UserStartedSpeakingFrame"/> is a continuation,
/// not a barge-in. The gate stays open. If the user resumes within the window — or a smart-turn confirmer rejects
/// the end-of-turn — the VAD re-emits this frame with <see cref="Superseded"/> = <c>true</c>; the STT processor
/// then drops any final tagged with that id before it becomes a <see cref="TranscriptionFrame"/> (the suppression
/// guarantee). Default <see cref="Superseded"/> = <c>false</c> (the arm/flush signal).
/// </summary>
public sealed record SpeculativeUtteranceFrame(long UtteranceId, bool Superseded = false) : SystemFrame;

/// <summary>
/// Recoverable or fatal error. By convention errors travel <see cref="FrameDirection.Upstream"/> so
/// the runner can surface them at the source. Use <see cref="Processors.FrameProcessor.PushErrorAsync"/>
/// from inside a processor — it sets the direction correctly.
/// </summary>
public sealed record ErrorFrame(string Message, Exception? InnerException = null) : SystemFrame;
