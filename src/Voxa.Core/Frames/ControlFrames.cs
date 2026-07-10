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
/// Eager/speculative end-of-utterance marker (VRT-002 WS1). Emitted by the VAD when unvoiced silence reaches
/// <c>EagerSttDelay</c> (strictly &lt; <c>StopDuration</c>): it tells <c>SpeechToTextProcessor</c> to flush the
/// buffered utterance speculatively for <see cref="UtteranceId"/> so transcription overlaps the rest of the
/// hangover. The gate stays open. If the user resumes within the window — or a smart-turn confirmer rejects the
/// end-of-turn — the VAD re-emits it with <see cref="Superseded"/> = <c>true</c>; the STT processor then drops
/// any final tagged with that id before it becomes a <see cref="TranscriptionFrame"/> (the suppression
/// guarantee). Default <see cref="Superseded"/> = <c>false</c> (the arm/flush signal).
///
/// <para>
/// Deliberately a <see cref="ControlFrame"/>, not a <see cref="SystemFrame"/>: it must stay FIFO-ordered with
/// the <see cref="AudioRawFrame"/>s on the data channel so the speculative flush sees ALL preceding audio. On
/// the priority/system channel it could overtake still-queued audio, and the peek/discard would drop speech.
/// </para>
/// </summary>
public sealed record SpeculativeUtteranceFrame(long UtteranceId, bool Superseded = false) : ControlFrame;

/// <summary>
/// Emitted once, immediately after the pipeline starts, to announce the audio sample rates
/// in use. The WebSocket sink serialises this as <c>{"type":"session","v":1,...}</c> so the
/// client can configure its AudioContext without hard-coding per-route constants.
/// Optional: clients that never receive it fall back to their constructor defaults (old servers
/// keep working with new clients; old clients ignore the envelope silently).
/// </summary>
public sealed record SessionInfoFrame(
    int InputSampleRate,
    int OutputSampleRate,
    int ProtocolVersion = 1) : ControlFrame;

/// <summary>
/// Emitted by <see cref="Processors.AgentLoopProcessor"/> at the start of a turn — i.e. just before
/// the host's <c>RunTurnAsync</c> begins streaming. Lets downstream processors (TTS, audit, metrics)
/// see clean turn boundaries instead of inferring them from text-frame arrival patterns.
///
/// <para>
/// Pipecat parity: <c>LLMFullResponseStartFrame</c>.
/// </para>
/// </summary>
public sealed record LlmTurnStartedFrame(
    string TurnId,
    TurnTrigger Trigger = TurnTrigger.UserUtterance) : ControlFrame;

/// <summary>
/// Emitted by <see cref="Processors.AgentLoopProcessor"/> after a turn completes — i.e. all re-run
/// iterations are done and the agent has produced its final assistant text. Mirrors
/// <see cref="LlmTurnStartedFrame"/>.
///
/// <para>
/// Pipecat parity: <c>LLMFullResponseEndFrame</c>.
/// </para>
/// </summary>
public sealed record LlmTurnEndedFrame(
    string TurnId,
    TurnTrigger Trigger = TurnTrigger.UserUtterance) : ControlFrame;

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
