using Voxa.Frames;

namespace Voxa.Processors;

/// <summary>
/// Runs one user turn: yields frames into the pipeline (text chunks, frontend tool requests, custom
/// frames via the emitter) and may pause to await frontend tool results via the gateway.
///
/// <para>
/// This is Voxa's framework-agnostic agent contract. The MAF adapter ships a concrete
/// implementation in <c>Voxa.Services.MicrosoftAgents</c>; future framework adapters
/// (Semantic Kernel, custom <c>IChatClient</c> loops, …) live as sibling implementations.
/// <see cref="AgentLoopProcessor"/> consumes any <see cref="IAgentTurnDriver"/> — it knows nothing
/// about the underlying agent framework.
/// </para>
/// </summary>
public interface IAgentTurnDriver
{
    /// <summary>
    /// Yield frames for one user turn. Ordering: caller (<see cref="AgentLoopProcessor"/>) emits
    /// <see cref="LlmTurnStartedFrame"/> before invoking this; emits <see cref="LlmTurnEndedFrame"/>
    /// after the iterator completes.
    /// </summary>
    /// <param name="ctx">Per-turn payload.</param>
    /// <param name="ct">Cancellation token, set when the connection ends or the turn is cancelled.</param>
    IAsyncEnumerable<Frame> RunTurnAsync(VoiceTurnContext ctx, CancellationToken ct);
}
