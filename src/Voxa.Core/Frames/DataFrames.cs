namespace Voxa.Frames;

/// <summary>Raw PCM audio chunk. Encoding fixed by <see cref="StartFrame"/> at session start.</summary>
public sealed record AudioRawFrame(ReadOnlyMemory<byte> Pcm, int SampleRate, int Channels) : DataFrame;

/// <summary>STT output. <c>IsFinal=false</c> for interim hypotheses, <c>true</c> when the utterance settles.</summary>
public sealed record TranscriptionFrame(
    string Text,
    bool IsFinal,
    string? Language = null,
    string? SpeakerId = null) : DataFrame;

/// <summary>Generic text chunk in the pipeline. Often produced by adapters or pre/post-processors.</summary>
public sealed record TextFrame(string Text) : DataFrame;

/// <summary>A token chunk emitted by an LLM agent processor. Distinct from <see cref="TextFrame"/> for routing.</summary>
public sealed record LlmTextChunkFrame(string Text) : DataFrame;

/// <summary>
/// Tool/function call requested by the LLM. <c>ArgumentsJson</c> is the raw JSON object.
///
/// <para>
/// Marked <see cref="IUninterruptible"/> so a mid-stream <see cref="InterruptionFrame"/>
/// doesn't drop an in-flight tool call. Pipecat parity: <c>FunctionCallInProgressFrame</c>
/// is uninterruptible there for the same reason — dropping a call mid-flight leaves
/// conversation state inconsistent.
/// </para>
/// </summary>
public sealed record ToolCallRequestFrame(
    string CallId,
    string Name,
    string ArgumentsJson) : DataFrame, IUninterruptible;

/// <summary>
/// Result of a tool/function call returned to the LLM. <c>ResultJson</c> is the serialized result.
///
/// <para>
/// Marked <see cref="IUninterruptible"/> so a mid-stream <see cref="InterruptionFrame"/>
/// doesn't drop the result before the agent loop can resume on it. Pipecat parity:
/// <c>FunctionCallResultFrame</c>.
/// </para>
/// </summary>
public sealed record ToolCallResultFrame(
    string CallId,
    string ResultJson,
    bool IsError = false) : DataFrame, IUninterruptible;

/// <summary>
/// Delegation request for a background agent (VDX-008): the interaction model hands long-running
/// work (tools, browsing, multi-step reasoning) to a heavyweight second driver so the voice path
/// stays responsive. Emitted by the interaction driver, forwarded downstream by
/// <see cref="Processors.AgentLoopProcessor"/>, consumed by
/// <see cref="Processors.BackgroundAgentProcessor"/>.
///
/// <para>
/// Uninterruptible for the same reason <see cref="ToolCallRequestFrame"/> is: a barge-in must not
/// orphan a task the model has already verbally promised.
/// </para>
/// </summary>
public sealed record BackgroundTaskRequestFrame(
    string TaskId,
    string Goal,
    string? ContextJson = null,
    string? OriginTurnId = null) : DataFrame, IUninterruptible;

/// <summary>
/// Completion notice for a delegated background task (VDX-008). Pushed upstream
/// (<see cref="FrameDirection.Upstream"/>, like <see cref="ErrorFrame"/>) by
/// <see cref="Processors.BackgroundAgentProcessor"/>; consumed by
/// <see cref="Processors.AgentLoopProcessor"/>, which re-enters it as a
/// <see cref="TurnTrigger.BackgroundResult"/> turn so the interaction model can gate relevance.
///
/// <para>
/// <c>ResultText</c> is a compact summary the interaction model reads inside a latency-bounded
/// voice turn — never the background driver's full trace. Token totals are two raw longs
/// (mirroring <see cref="LlmUsageFrame"/>) so the frame doesn't reference processor types.
/// </para>
/// </summary>
public sealed record BackgroundTaskCompletedFrame(
    string TaskId,
    string ResultText,
    bool IsError = false,
    long ElapsedMs = 0,
    long InputTokens = 0,
    long OutputTokens = 0,
    string? OriginTurnId = null) : DataFrame, IUninterruptible;
