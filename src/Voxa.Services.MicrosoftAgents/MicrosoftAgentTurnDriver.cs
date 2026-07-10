using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Voxa.Frames;
using Voxa.Processors;

namespace Voxa.Services.MicrosoftAgents;

/// <summary>
/// Internal MAF-specific implementation of <see cref="IAgentTurnDriver"/>. Wraps a
/// <see cref="AIAgent"/> and translates each <see cref="ChatResponseUpdate"/> into Voxa frames,
/// pausing on frontend-tool calls until the result lands and re-running the agent inline with the
/// returned <see cref="FunctionResultContent"/>.
///
/// <para>
/// Constructed by <see cref="MicrosoftAgentVoice.CreateProcessor"/>. Hosts never instantiate this
/// directly; they configure behavior via <see cref="MicrosoftAgentVoiceOptions"/>.
/// </para>
/// </summary>
internal sealed class MicrosoftAgentTurnDriver : IAgentTurnDriver
{
    private readonly AIAgent _agent;
    private readonly MicrosoftAgentVoiceOptions _options;
    private readonly ILogger _logger;

    public MicrosoftAgentTurnDriver(
        AIAgent agent,
        MicrosoftAgentVoiceOptions? options = null,
        ILogger? logger = null)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _options = options ?? new MicrosoftAgentVoiceOptions();
        _logger = logger ?? NullLogger.Instance;
    }

    public async IAsyncEnumerable<Frame> RunTurnAsync(
        VoiceTurnContext ctx,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var messages = await BuildMessagesAsync(ctx, ct).ConfigureAwait(false);
        var runOptions = _options.BuildRunOptions?.Invoke(ctx);
        if (_options.EnableBackgroundDelegation && ctx.Trigger == TurnTrigger.UserUtterance)
        {
            // VDX-008: delegation is a per-turn backend tool bound to THIS turn's emitter, so the
            // request frame rides the pipeline exactly like the driver's own yields.
            runOptions = WithDelegateTool(runOptions, ctx, ct);
        }
        var isFrontendTool = _options.IsFrontendTool ?? (static _ => false);

        // Re-run loop: the agent emits text + function calls; if any of those are frontend tools we
        // pause to await results, then re-invoke RunStreamingAsync with the assistant call + tool
        // result messages appended. Mirrors AGUI's re-run pattern but inline within one turn.
        while (true)
        {
            var pendingFrontend = new List<(FunctionCallContent Call, ValueTask<ToolCallResultFrame> ResultTask)>();

            await foreach (var update in _agent.RunStreamingAsync(
                               messages, session: null, options: runOptions, cancellationToken: ct).ConfigureAwait(false))
            {
                var chatUpdate = update.AsChatResponseUpdate();
                if (chatUpdate?.Contents is null) continue;

                foreach (var content in chatUpdate.Contents)
                {
                    switch (content)
                    {
                        case TextContent text when !string.IsNullOrEmpty(text.Text):
                            yield return new LlmTextChunkFrame(text.Text);
                            break;

                        case FunctionCallContent fn when isFrontendTool(fn.Name ?? string.Empty):
                            // Frontend tool — register pending wait, yield request frame.
                            // CallId normalization: MAF sometimes omits CallId; we mint one and pair
                            // it with the assistant message we'll append for the re-run.
                            var callId = !string.IsNullOrWhiteSpace(fn.CallId)
                                ? fn.CallId
                                : Guid.NewGuid().ToString("N");
                            var normalizedCall = new FunctionCallContent(callId, fn.Name ?? string.Empty, fn.Arguments);

                            var awaitTask = ctx.FrontendTools.AwaitToolResultAsync(callId, ct);
                            pendingFrontend.Add((normalizedCall, awaitTask));

                            yield return new ToolCallRequestFrame(
                                callId,
                                fn.Name ?? string.Empty,
                                SerializeArguments(fn.Arguments));
                            break;

                        case FunctionCallContent fn:
                            // Backend tool — MAF will auto-execute synchronously inside this
                            // RunStreamingAsync call. Optionally surface a sanitized status to the
                            // client UI ("Checking your spending...") so end users see something
                            // happening while the tool runs. Raw FunctionCallContent / matching
                            // FunctionResultContent are NOT yielded as frames — the spec is explicit
                            // that raw tool names must not leak to consumer UI.
                            if (_options.BuildBackendToolStatus is { } buildStatus
                                && buildStatus(fn.Name ?? string.Empty) is { Length: > 0 } statusMessage)
                            {
                                yield return new StatusFrame(statusMessage);
                            }
                            break;

                        case UsageContent usageContent:
                            // Stream token totals into the loop's TurnSummary aggregation.
                            yield return new LlmUsageFrame(
                                InputTokens: usageContent.Details?.InputTokenCount ?? 0,
                                OutputTokens: usageContent.Details?.OutputTokenCount ?? 0);
                            break;

                        default:
                            break;
                    }
                }
            }

            if (pendingFrontend.Count == 0)
            {
                yield break;
            }

            // Wait for all pending frontend-tool results. Safe: AgentLoopProcessor's data loop is
            // still draining, completing TCSs as ToolCallResultFrames arrive on the source.
            var results = await ResolvePendingAsync(pendingFrontend).ConfigureAwait(false);

            // Append the assistant call message + tool result messages, then loop back into
            // RunStreamingAsync.
            var augmented = messages.ToList();
            augmented.Add(new ChatMessage(
                ChatRole.Assistant,
                pendingFrontend.Select(p => (AIContent)p.Call).ToList()));
            for (int i = 0; i < results.Length; i++)
            {
                var resultJson = results[i].ResultJson;
                augmented.Add(new ChatMessage(
                    ChatRole.Tool,
                    new AIContent[] { new FunctionResultContent(pendingFrontend[i].Call.CallId!, resultJson) }));
            }
            messages = augmented;
        }
    }

    private async ValueTask<IReadOnlyList<ChatMessage>> BuildMessagesAsync(
        VoiceTurnContext ctx, CancellationToken ct)
    {
        if (_options.BuildMessages is { } build)
        {
            return await build(ctx, ct).ConfigureAwait(false);
        }
        // VDX-008: a background-result turn carries no user text — feed the result + relevance-gate
        // instruction instead, or the model would run an empty turn and the result would be lost.
        if (ctx.Trigger == TurnTrigger.BackgroundResult && ctx.BackgroundResult is { } result)
        {
            return new[] { MicrosoftAgentVoice.CreateBackgroundResultMessage(result) };
        }
        // Default: single user message. Sufficient for stateless one-shot agents.
        return new ChatMessage[] { new(ChatRole.User, ctx.UserText) };
    }

    /// <summary>
    /// Append the VDX-008 <c>delegate_task</c> backend tool to this turn's run options. The ack
    /// return value carries the prompt contract (§7): acknowledge, don't fabricate — models read
    /// tool results far more reliably than system-prompt addenda.
    /// </summary>
    private static ChatClientAgentRunOptions WithDelegateTool(
        ChatClientAgentRunOptions? runOptions, VoiceTurnContext ctx, CancellationToken ct)
    {
        var tool = AIFunctionFactory.Create(
            async (string goal, string? context_summary) =>
            {
                var taskId = Guid.NewGuid().ToString("N");
                await ctx.Emitter.EmitAsync(
                    new BackgroundTaskRequestFrame(taskId, goal, context_summary, ctx.TurnId),
                    ct).ConfigureAwait(false);
                return $"Delegated (task {taskId}). The result will arrive shortly as a system note. " +
                       "Acknowledge to the user that you're working on it — do NOT invent the answer yourself.";
            },
            name: "delegate_task",
            description: "Hand off work that needs tools, browsing, or extended reasoning to a background " +
                         "assistant so the conversation keeps flowing. Pass a self-contained goal and a compact " +
                         "context_summary (a sentence or two — never the whole conversation).");

        runOptions ??= new ChatClientAgentRunOptions();
        var chatOptions = runOptions.ChatOptions ??= new ChatOptions();
        chatOptions.Tools ??= new List<AITool>();
        chatOptions.Tools.Add(tool);
        return runOptions;
    }

    private static async Task<ToolCallResultFrame[]> ResolvePendingAsync(
        List<(FunctionCallContent Call, ValueTask<ToolCallResultFrame> ResultTask)> pending)
    {
        var tasks = new Task<ToolCallResultFrame>[pending.Count];
        for (int i = 0; i < pending.Count; i++)
        {
            tasks[i] = pending[i].ResultTask.AsTask();
        }
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static string SerializeArguments(IDictionary<string, object?>? arguments)
        => arguments is null ? "{}" : JsonSerializer.Serialize(arguments);
}
