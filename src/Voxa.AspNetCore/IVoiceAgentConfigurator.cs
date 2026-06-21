using Microsoft.Extensions.DependencyInjection;
using Voxa.Services.MicrosoftAgents;

namespace Voxa.AspNetCore;

/// <summary>
/// Host seam (VDX-006) for customizing the voice agent's per-turn options under <c>UseDefaults()</c> —
/// a durable conversation store, frontend tools, or turn-lifecycle hooks — without abandoning
/// <see cref="DefaultVoicePipelineComposer"/>. Resolved per connection from the session scope; when none
/// is registered the composer keeps its built-in <c>InMemoryChatHistory</c> (the byte-identical default).
/// </summary>
public interface IVoiceAgentConfigurator
{
    /// <summary>Mutate the agent options for this connection. The composer's defaults are already applied;
    /// the host has the last word (it may replace <c>BuildMessages</c>, <c>OnTurnCompleted</c>, etc.).</summary>
    /// <param name="connection">The per-connection service provider — HTTP <c>RequestServices</c>, or the
    /// in-proc session scope under Studio. Resolve scoped state from it (e.g. the active thread id); a
    /// singleton configurator still gets per-connection state this way, since this runs once per connection.</param>
    /// <param name="options">The agent options to mutate.</param>
    void Configure(IServiceProvider connection, MicrosoftAgentVoiceOptions options);
}

/// <summary>Registration helpers for <see cref="IVoiceAgentConfigurator"/> (VDX-006).</summary>
public static class VoiceAgentConfiguratorExtensions
{
    /// <summary>
    /// Register a delegate-based <see cref="IVoiceAgentConfigurator"/> for hosts that prefer a lambda to a
    /// class. The delegate runs once per connection; resolve per-connection state from the supplied provider.
    /// </summary>
    public static IServiceCollection AddVoxaVoiceAgentConfigurator(
        this IServiceCollection services,
        Action<IServiceProvider, MicrosoftAgentVoiceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        return services.AddSingleton<IVoiceAgentConfigurator>(new DelegateVoiceAgentConfigurator(configure));
    }

    private sealed class DelegateVoiceAgentConfigurator(
        Action<IServiceProvider, MicrosoftAgentVoiceOptions> configure) : IVoiceAgentConfigurator
    {
        public void Configure(IServiceProvider connection, MicrosoftAgentVoiceOptions options)
            => configure(connection, options);
    }
}
