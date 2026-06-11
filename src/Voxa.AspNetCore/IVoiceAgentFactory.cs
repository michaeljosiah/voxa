using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Http;

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
    AIAgent Create(HttpContext context, VoxaAgentOptions options);

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
