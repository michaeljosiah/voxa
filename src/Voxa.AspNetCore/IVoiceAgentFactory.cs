using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Http;

namespace Voxa.AspNetCore;

/// <summary>
/// Creates the per-connection default agent when neither an <c>AIAgent</c> nor an
/// <c>IChatClient</c> is registered in DI. Implement this interface and register it as
/// a singleton to provide a custom provider-backed default agent.
/// The Voxa meta-package registers <c>OpenAIChatAgentFactory</c> which handles
/// <c>Voxa:Agent:Provider == "OpenAI"</c>.
/// </summary>
public interface IVoiceAgentFactory
{
    AIAgent Create(HttpContext context, VoxaAgentOptions options);
}
