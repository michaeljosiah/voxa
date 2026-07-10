using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Voxa.Frames;
using Voxa.Processors;

namespace Voxa.Services.MicrosoftAgents;

/// <summary>
/// Optional host hooks for <see cref="MicrosoftAgentVoice.CreateProcessor"/>. Every property is
/// <c>null</c> by default, in which case the adapter falls back to the simplest sensible behavior
/// for that hook. Most hosts only override two or three of these.
///
/// <para>
/// The adapter calls hooks at well-defined points in the per-turn flow:
/// </para>
/// <list type="number">
///   <item><see cref="BuildMessages"/> — at turn start to assemble the messages MAF sees.
///       Default: a single <c>new ChatMessage(ChatRole.User, ctx.UserText)</c>.</item>
///   <item><see cref="BuildRunOptions"/> — at turn start to assemble the run options MAF sees.
///       Default: <c>null</c> (agent defaults).</item>
///   <item><see cref="IsFrontendTool"/> — once per <see cref="FunctionCallContent"/> the agent emits.
///       Default: <c>false</c> for everything (treat all tools as MAF-auto-executed).</item>
///   <item><see cref="OnTurnStarted"/> — fired by <see cref="AgentLoopProcessor"/> before the
///       driver runs. Default: no-op.</item>
///   <item><see cref="OnTurnCompleted"/> — fired by <see cref="AgentLoopProcessor"/> after the
///       driver completes successfully. Default: no-op.</item>
///   <item><see cref="OnTurnFailed"/> — fired by <see cref="AgentLoopProcessor"/> when the driver
///       throws. Default: no-op (the loop still emits an upstream <see cref="ErrorFrame"/>).</item>
/// </list>
/// </summary>
public sealed class MicrosoftAgentVoiceOptions
{
    /// <summary>
    /// Build the message list for this turn. Hosts that persist conversations override this to
    /// load history, prepend a system message / user-context preamble, etc.
    /// </summary>
    public Func<VoiceTurnContext, CancellationToken, ValueTask<IReadOnlyList<ChatMessage>>>? BuildMessages { get; set; }

    /// <summary>
    /// Build the run options for this turn (frontend-tool declarations, model override,
    /// telemetry property stamping). Returning <c>null</c> means "use agent defaults".
    /// </summary>
    public Func<VoiceTurnContext, ChatClientAgentRunOptions?>? BuildRunOptions { get; set; }

    /// <summary>
    /// Classify a tool name as frontend (round-trip through pipeline → client) vs backend
    /// (MAF auto-executes). Default: all tools are backend (i.e. MAF handles them inline).
    /// </summary>
    public Func<string, bool>? IsFrontendTool { get; set; }

    /// <summary>
    /// Map a backend (MAF auto-executed) tool name to a sanitized, user-facing progress message.
    /// Lets the natural conversational pattern surface a status envelope to the client UI ("Checking
    /// your spending...") while MAF executes the tool. Returning <c>null</c> suppresses the status
    /// for that tool — the agent's pre-tool acknowledgement text alone carries the conversational
    /// beat.
    ///
    /// <para>
    /// <strong>Do not return raw tool names</strong> (e.g. <c>pf_get_spending_summary</c>); this
    /// string is rendered to end users. Drivers emit this as a <see cref="StatusFrame"/> through
    /// the pipeline so transports can wrap it in their own envelope (the WebSocket sink ships with
    /// a <c>{ "type": "status", "message": "..." }</c> envelope by default).
    /// </para>
    /// </summary>
    public Func<string, string?>? BuildBackendToolStatus { get; set; }

    /// <summary>Fired before the driver streams the turn. Hooked from <see cref="AgentLoopProcessor"/>.</summary>
    public Func<VoiceTurnContext, CancellationToken, ValueTask>? OnTurnStarted { get; set; }

    /// <summary>Fired after a successful turn with the produced summary. Hooked from <see cref="AgentLoopProcessor"/>.</summary>
    public Func<VoiceTurnContext, TurnSummary, CancellationToken, ValueTask>? OnTurnCompleted { get; set; }

    /// <summary>Fired when the driver throws. Hooked from <see cref="AgentLoopProcessor"/>.</summary>
    public Func<VoiceTurnContext, Exception, CancellationToken, ValueTask>? OnTurnFailed { get; set; }

    /// <summary>
    /// Optional response-duration cap (VRT-002 WS2 §6.5), passed through to
    /// <see cref="AgentLoopProcessor"/>. When set, a single turn stops pumping the driver's yielded frames
    /// once its wall-clock elapsed time reaches this bound and closes cleanly. Null ⇒ no cap (default).
    /// </summary>
    public TimeSpan? MaxResponseDuration { get; set; }

    /// <summary>
    /// VDX-008: inject a <c>delegate_task(goal, context_summary)</c> backend tool into every
    /// user-utterance turn. Its handler emits a <see cref="BackgroundTaskRequestFrame"/> through the
    /// pipeline and returns an acknowledgment string — so to the model, delegation is just a fast
    /// tool call. Requires a <c>BackgroundAgentProcessor</c> downstream to consume the request.
    /// The tool is backend (MAF auto-executes it); do not classify it via <see cref="IsFrontendTool"/>.
    /// </summary>
    public bool EnableBackgroundDelegation { get; set; }

    /// <summary>
    /// VDX-008 §4.1 arbitration knobs, passed through to <see cref="AgentLoopProcessor"/>.
    /// Null ⇒ loop defaults.
    ///
    /// <para>
    /// <strong>Overriding <see cref="BuildMessages"/> takes on the trigger check:</strong> for
    /// <see cref="Frames.TurnTrigger.BackgroundResult"/> turns <c>ctx.UserText</c> is empty — read
    /// <c>ctx.BackgroundResult</c> and include its <c>ResultText</c> (the helper
    /// <see cref="MicrosoftAgentVoice.CreateBackgroundResultMessage"/> does this), or the model
    /// never sees the result.
    /// </para>
    /// </summary>
    public BackgroundResultOptions? BackgroundResults { get; set; }

    /// <summary>Optional diagnostics hub for background-result drop events, passed through to
    /// <see cref="AgentLoopProcessor"/>. Null ⇒ no diagnostics.</summary>
    public Voxa.Diagnostics.VoxaDiagnosticsHub? DiagnosticsHub { get; set; }
}
