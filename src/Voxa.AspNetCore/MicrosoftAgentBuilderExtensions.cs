using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Http;
using Voxa.Services.MicrosoftAgents;

namespace Voxa.AspNetCore;

/// <summary>
/// Fluent helpers that wire a Microsoft Agent Framework <see cref="AIAgent"/> into a Voxa voice
/// pipeline. The simplest form (<c>UseMicrosoftAgent(agent)</c>) covers stateless agents in two
/// lines; advanced overloads accept a configurator delegate that lets hosts override
/// <c>BuildMessages</c>, <c>BuildRunOptions</c>, <c>IsFrontendTool</c>, and the turn lifecycle
/// hooks (<c>OnTurnStarted</c> / <c>OnTurnCompleted</c> / <c>OnTurnFailed</c>).
/// </summary>
public static class MicrosoftAgentBuilderExtensions
{
    /// <summary>
    /// Drive the pipeline with the supplied <paramref name="agent"/>. Default behavior: each turn
    /// runs <c>RunStreamingAsync</c> with a single user message built from the transcription.
    /// Hosts that need persisted history, frontend tools, or post-turn audit pass a
    /// <paramref name="configure"/> callback.
    /// </summary>
    public static VoicePipelineBuilder UseMicrosoftAgent(
        this VoicePipelineBuilder builder,
        AIAgent agent,
        Action<MicrosoftAgentVoiceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return builder.UseProcessor(() => MicrosoftAgentVoice.CreateProcessor(agent, configure));
    }

    /// <summary>
    /// Drive the pipeline with an agent resolved per-connection from the request
    /// <see cref="HttpContext"/>. Use this overload when the agent depends on scoped DI services,
    /// the parsed hello envelope (<c>VoiceHello.HelloMetadataKey</c>), or the authenticated
    /// principal — anything that's not stable for the process lifetime.
    /// </summary>
    public static VoicePipelineBuilder UseMicrosoftAgent(
        this VoicePipelineBuilder builder,
        Func<HttpContext, AIAgent> agentFactory,
        Action<HttpContext, MicrosoftAgentVoiceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(agentFactory);
        return builder.UseProcessor(ctx =>
        {
            var agent = agentFactory(ctx);
            return MicrosoftAgentVoice.CreateProcessor(
                agent,
                configure is null ? null : opts => configure(ctx, opts));
        });
    }
}
