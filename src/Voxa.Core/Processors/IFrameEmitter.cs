using Voxa.Frames;

namespace Voxa.Processors;

/// <summary>
/// Lets a turn driver emit frames out-of-band (i.e. outside its <c>RunTurnAsync</c> yield stream).
/// Useful for hosts that need to push a custom frame type from inside a hook — e.g. AONIK emitting a
/// <c>ThreadReadyFrame</c> from inside its <c>BuildMessages</c> delegate before the agent has
/// produced any text.
///
/// <para>
/// The implementation forwards through the same downstream link as
/// <see cref="AgentLoopProcessor"/>'s yielded frames, so the sink's send-lock discipline is
/// preserved.
/// </para>
/// </summary>
public interface IFrameEmitter
{
    /// <summary>
    /// Push <paramref name="frame"/> downstream. Honors the frame's <see cref="Frame.Direction"/>.
    /// </summary>
    ValueTask EmitAsync(Frame frame, CancellationToken ct);
}
