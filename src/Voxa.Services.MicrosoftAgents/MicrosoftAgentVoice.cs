using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Voxa.Processors;

namespace Voxa.Services.MicrosoftAgents;

/// <summary>
/// Public factory for plugging a Microsoft Agent Framework <see cref="AIAgent"/> into a Voxa
/// pipeline. Returns a configured <see cref="AgentLoopProcessor"/> ready to <c>.Then(...)</c> into
/// the pipeline builder.
///
/// <para>
/// The simple case is one line:
/// </para>
///
/// <code>
/// pipeline.Then(MicrosoftAgentVoice.CreateProcessor(agent));
/// </code>
///
/// <para>
/// Hosts that need turn lifecycle hooks, custom message construction, model override, or
/// frontend-tool routing pass an options configurator:
/// </para>
///
/// <code>
/// pipeline.Then(MicrosoftAgentVoice.CreateProcessor(agent, options =>
/// {
///     options.BuildMessages = BuildMyMessagesAsync;
///     options.BuildRunOptions = BuildMyRunOptions;
///     options.IsFrontendTool = name => myCatalog.Contains(name);
///     options.OnTurnCompleted = PersistMyTurnAsync;
/// }));
/// </code>
///
/// <para>
/// Voxa owns the deadlock-safe data-loop / turn-worker split, frontend-tool TCS correlation,
/// agent re-invocation loop, and turn-boundary frames. Hosts only contribute the things that are
/// genuinely host-specific (which messages, which run options, which tools are frontend tools,
/// what to do at turn boundaries).
/// </para>
/// </summary>
public static class MicrosoftAgentVoice
{
    /// <summary>
    /// Create a configured <see cref="AgentLoopProcessor"/> driving the supplied
    /// <paramref name="agent"/>. The optional <paramref name="configure"/> callback lets hosts
    /// customize behavior via <see cref="MicrosoftAgentVoiceOptions"/>.
    /// </summary>
    /// <param name="agent">The MAF agent to drive.</param>
    /// <param name="configure">Optional. Configure delegate hooks; called once at construction.</param>
    /// <param name="logger">Optional logger for the internal driver.</param>
    public static AgentLoopProcessor CreateProcessor(
        AIAgent agent,
        Action<MicrosoftAgentVoiceOptions>? configure = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(agent);

        var options = new MicrosoftAgentVoiceOptions();
        configure?.Invoke(options);

        var driver = new MicrosoftAgentTurnDriver(agent, options, logger);

        return new AgentLoopProcessor(
            driver,
            onTurnStarted: options.OnTurnStarted,
            onTurnCompleted: options.OnTurnCompleted,
            onTurnFailed: options.OnTurnFailed,
            maxResponseDuration: options.MaxResponseDuration);
    }
}
