using Microsoft.Extensions.DependencyInjection;
using Voxa.Processors;

namespace Voxa.AspNetCore;

/// <summary>
/// Registration sugar for the VDX-008 background agent (talker/thinker split). Registering a
/// background driver is the whole opt-in: the composer inserts the <see cref="BackgroundAgentProcessor"/>
/// stage, arms the agent loop's arbitration, and (on the Microsoft-Agents path) injects the
/// <c>delegate_task</c> tool. Unregistered ⇒ the composed pipeline is byte-identical to today.
/// </summary>
public static class VoxaBackgroundAgentExtensions
{
    /// <summary>
    /// Register the background <see cref="IAgentTurnDriver"/> under the composer's keyed seam.
    /// Scoped: one driver instance per connection — background drivers typically hold
    /// per-conversation state. A stateless, thread-safe driver may instead be registered manually
    /// with <c>AddKeyedSingleton</c> under <see cref="VoxaBackgroundAgentOptions.ServiceKey"/>.
    /// Tuning lives under <c>Voxa:BackgroundAgent:*</c>.
    /// </summary>
    public static IServiceCollection AddVoxaBackgroundAgent(
        this IServiceCollection services,
        Func<IServiceProvider, IAgentTurnDriver> driverFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(driverFactory);
        services.AddKeyedScoped(VoxaBackgroundAgentOptions.ServiceKey, (sp, _) => driverFactory(sp));
        return services;
    }
}
