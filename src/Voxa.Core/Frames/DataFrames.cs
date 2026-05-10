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
