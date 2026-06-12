using Microsoft.Agents.AI;

namespace Voxa.AspNetCore;

/// <summary>
/// Creates the per-connection default agent when neither an <c>AIAgent</c> nor an
/// <c>IChatClient</c> is registered in DI. Implement this interface and register it as
/// a singleton to provide a custom provider-backed default agent.
/// The Voxa meta-package registers a default factory which handles
/// <c>Voxa:Agent:Provider == "OpenAI"</c> (chat completions) and <c>"Echo"</c> (keyless
/// diagnostic agent for demos and CI).
/// </summary>
public interface IVoiceAgentFactory
{
    /// <summary>
    /// Create the agent for one session. <paramref name="services"/> is the session's scope —
    /// an HTTP host passes the connection's <c>RequestServices</c>; non-HTTP hosts (Voxa Studio)
    /// pass their own session scope. (VST-001 WS0 made this transport-agnostic; it previously
    /// took an <c>HttpContext</c>.)
    /// </summary>
    AIAgent Create(IServiceProvider services, VoxaAgentOptions options);

    /// <summary>
    /// Startup validation called by <c>VoxaDefaultsGuard</c> when no <c>AIAgent</c> /
    /// <c>IChatClient</c> is registered in DI and this factory will be the agent source.
    /// Return one message per problem that would make <see cref="Create"/> throw
    /// (unsupported <c>Voxa:Agent:Provider</c>, missing credentials, …); empty means usable.
    /// Default implementation reports no errors, which preserves the presence-only check
    /// for factories that cannot validate ahead of time.
    /// </summary>
    IReadOnlyList<string> Validate(VoxaAgentOptions options) => Array.Empty<string>();
}
