namespace Voxa.Frames;

/// <summary>
/// Initial frame injected by <see cref="Pipelines.PipelineRunner"/> when a session starts.
/// Carries the negotiated audio configuration so processors can configure their codecs.
/// </summary>
public sealed record StartFrame(
    int? SampleRate = null,
    int? Channels = null) : ControlFrame;

/// <summary>
/// Graceful shutdown signal. When observed at the sink, the runner's <c>WaitAsync</c> completes.
/// Marked uninterruptible so it survives a mid-flight <see cref="InterruptionFrame"/>.
/// </summary>
public sealed record EndFrame : ControlFrame, IUninterruptible;

/// <summary>Optional keepalive — transports may emit periodically to detect dead connections.</summary>
public sealed record HeartbeatFrame : ControlFrame;

/// <summary>
/// Emitted by <see cref="Processors.AgentLoopProcessor"/> at the start of a turn — i.e. just before
/// the host's <c>RunTurnAsync</c> begins streaming. Lets downstream processors (TTS, audit, metrics)
/// see clean turn boundaries instead of inferring them from text-frame arrival patterns.
///
/// <para>
/// Pipecat parity: <c>LLMFullResponseStartFrame</c>.
/// </para>
/// </summary>
public sealed record LlmTurnStartedFrame(string TurnId) : ControlFrame;

/// <summary>
/// Emitted by <see cref="Processors.AgentLoopProcessor"/> after a turn completes — i.e. all re-run
/// iterations are done and the agent has produced its final assistant text. Mirrors
/// <see cref="LlmTurnStartedFrame"/>.
///
/// <para>
/// Pipecat parity: <c>LLMFullResponseEndFrame</c>.
/// </para>
/// </summary>
public sealed record LlmTurnEndedFrame(string TurnId) : ControlFrame;

/// <summary>
/// Emitted by an <see cref="Processors.IAgentTurnDriver"/> when the underlying LLM provider reports
/// a token-usage delta for the current turn. Consumed by <see cref="Processors.AgentLoopProcessor"/>
/// (NOT forwarded downstream) and aggregated into <see cref="Processors.TurnSummary.Usage"/> so hosts
/// can record per-turn token totals from <c>OnTurnCompleted</c>.
///
/// <para>
/// Drivers may yield this any number of times per turn (most providers stream one update at the
/// end; some interleave deltas mid-stream). Values are additive.
/// </para>
/// </summary>
public sealed record LlmUsageFrame(long InputTokens, long OutputTokens) : ControlFrame;

/// <summary>
/// Sanitized progress / status update for the client UI. Designed for the natural conversational
/// pattern where the agent acknowledges a request, runs a backend tool, then speaks the result —
/// hosts emit a <c>StatusFrame</c> from inside their backend-tool dispatch (or from a Voxa adapter's
/// backend-tool hook) so the client can render "Checking your spending..." while the tool runs.
///
/// <para>
/// Transport-neutral by construction: hosts that don't want a status envelope on the wire can
/// configure their sink to drop these frames; the WebSocket sink ships with a default
/// <c>{ "type": "status", "message": "..." }</c> envelope.
/// </para>
///
/// <para>
/// <strong>Do not embed raw tool names</strong> (e.g. <c>pf_get_spending_summary</c>) in the
/// message — this frame is user-facing.
/// </para>
/// </summary>
public sealed record StatusFrame(string Message) : ControlFrame;
