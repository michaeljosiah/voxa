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
/// Recoverable or fatal error. By convention errors travel <see cref="FrameDirection.Upstream"/> so
/// the runner can surface them at the source. Use <see cref="Processors.FrameProcessor.PushErrorAsync"/>
/// from inside a processor — it sets the direction correctly.
/// </summary>
public sealed record ErrorFrame(string Message, Exception? InnerException = null) : SystemFrame;
