namespace Voxa.Processors;

/// <summary>
/// What a turn produced. Built by <see cref="AgentLoopProcessor"/> from the frames yielded by the
/// host's <see cref="IAgentTurnDriver.RunTurnAsync"/>, then handed to the host's optional
/// <c>OnTurnCompleted</c> hook (set on <c>MicrosoftAgentVoiceOptions</c> for the MAF adapter).
///
/// <para>
/// Voxa fills these from observed frames: <c>AssistantText</c> from the concatenation of yielded
/// <see cref="Frames.LlmTextChunkFrame"/>s; token counts from <see cref="UsageTotals"/> the driver
/// records explicitly via <see cref="VoiceTurnContext.Metadata"/> (e.g. MAF's <c>UsageContent</c>);
/// elapsed time from a stopwatch around the whole <c>RunTurnAsync</c> invocation.
/// </para>
/// </summary>
public sealed record TurnSummary(
    string TurnId,
    string AssistantText,
    long ElapsedMs,
    UsageTotals Usage);

/// <summary>Aggregated token counts across all agent invocations in a turn (re-runs included).</summary>
public sealed record UsageTotals(long InputTokens, long OutputTokens)
{
    public static UsageTotals Empty { get; } = new(0, 0);

    public UsageTotals Add(long inputDelta, long outputDelta)
        => new(InputTokens + inputDelta, OutputTokens + outputDelta);
}
