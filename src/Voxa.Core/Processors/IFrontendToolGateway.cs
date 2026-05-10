using Voxa.Frames;

namespace Voxa.Processors;

/// <summary>
/// Back-channel that lets a turn driver yield a <see cref="ToolCallRequestFrame"/> downstream
/// (toward the client) and then await the matching <see cref="ToolCallResultFrame"/> coming back
/// upstream — without blocking the host pipeline's data loop.
///
/// <para>
/// The implementation lives inside <see cref="AgentLoopProcessor"/>; it correlates pending
/// <c>callId</c>s to incoming <see cref="ToolCallResultFrame"/>s as they arrive on the data loop and
/// completes the matching <see cref="System.Threading.Tasks.TaskCompletionSource"/>. The
/// continuation runs on the turn worker (TCSs are created with
/// <see cref="System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously"/>) so the
/// data loop is never blocked by host code.
/// </para>
/// <para>
/// This is the primitive that replaces the "every host writes its own deadlock-prone TCS dictionary"
/// boilerplate.
/// </para>
/// </summary>
public interface IFrontendToolGateway
{
    /// <summary>
    /// Returns a task that completes when a <see cref="ToolCallResultFrame"/> with the matching
    /// <paramref name="callId"/> arrives at the source. The caller is expected to have already
    /// emitted (or be about to emit) a <see cref="ToolCallRequestFrame"/> with the same id.
    /// </summary>
    /// <param name="callId">Tool call id to await. Must match a previously emitted request.</param>
    /// <param name="ct">
    /// Cancellation token. If cancelled, the returned task transitions to
    /// <see cref="System.Threading.Tasks.TaskStatus.Canceled"/> and any pending registration is
    /// removed.
    /// </param>
    ValueTask<ToolCallResultFrame> AwaitToolResultAsync(string callId, CancellationToken ct);
}
