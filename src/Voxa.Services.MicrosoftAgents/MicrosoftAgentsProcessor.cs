using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Voxa.Frames;
using Voxa.Processors;

namespace Voxa.Services.MicrosoftAgents;

/// <summary>
/// Wraps a Microsoft Agent Framework <see cref="AIAgent"/> as a Voxa <see cref="FrameProcessor"/>.
/// Each final-form transcription or text frame becomes a user message; the agent's streamed
/// response is fanned out to the downstream pipeline as
/// <see cref="LlmTextChunkFrame"/> + <see cref="ToolCallRequestFrame"/>.
///
/// <para>
/// Use this processor for the granular STT → Agent → TTS path (Azure Speech etc.). The Voice Live
/// composite path embeds the agent server-side and uses the Voice Live processor instead.
/// </para>
/// </summary>
public sealed class MicrosoftAgentsProcessor : FrameProcessor
{
    private readonly AIAgent _agent;
    private readonly AgentSession? _session;
    private readonly ILogger<MicrosoftAgentsProcessor> _logger;

    /// <param name="agent">Any Microsoft Agent Framework agent. Typically a <see cref="ChatClientAgent"/>.</param>
    /// <param name="session">
    /// Optional per-pipeline session for conversation history. Create one via
    /// <c>ChatClientAgent.CreateSessionAsync</c>. Pass null for stateless turns.
    /// </param>
    /// <param name="logger">Optional logger.</param>
    public MicrosoftAgentsProcessor(
        AIAgent agent,
        AgentSession? session = null,
        ILogger<MicrosoftAgentsProcessor>? logger = null)
        : base("MicrosoftAgents")
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _session = session;
        _logger = logger ?? NullLogger<MicrosoftAgentsProcessor>.Instance;
    }

    protected override async ValueTask ProcessFrameAsync(Frame frame, CancellationToken ct)
    {
        switch (frame)
        {
            case TranscriptionFrame t when t.IsFinal && !string.IsNullOrWhiteSpace(t.Text):
                // Forward the transcription downstream BEFORE running the agent so transports
                // can render the user's bubble immediately. Otherwise the bot's reply text
                // arrives at the sink first (LLM streams while we still hold the frame) and
                // the user transcript appears AFTER the bot has already replied.
                await PushFrameAsync(frame, ct).ConfigureAwait(false);
                await RunAgentAsync(t.Text, ct).ConfigureAwait(false);
                return;
            case TextFrame txt when !string.IsNullOrWhiteSpace(txt.Text):
                await RunAgentAsync(txt.Text, ct).ConfigureAwait(false);
                return;
        }

        // Forward StartFrame, EndFrame, interim transcriptions, etc. so the sink can complete.
        await PushFrameAsync(frame, ct).ConfigureAwait(false);
    }

    private async Task RunAgentAsync(string userInput, CancellationToken ct)
    {
        var messages = new[] { new ChatMessage(ChatRole.User, userInput) };

        try
        {
            await foreach (var update in _agent.RunStreamingAsync(messages, _session, options: null, ct).ConfigureAwait(false))
            {
                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case TextContent text when !string.IsNullOrEmpty(text.Text):
                            await PushFrameAsync(new LlmTextChunkFrame(text.Text), ct).ConfigureAwait(false);
                            break;

                        case FunctionCallContent func:
                            var argsJson = func.Arguments is { Count: > 0 }
                                ? JsonSerializer.Serialize(func.Arguments)
                                : "{}";
                            await PushFrameAsync(
                                new ToolCallRequestFrame(func.CallId ?? string.Empty, func.Name ?? string.Empty, argsJson),
                                ct).ConfigureAwait(false);
                            break;
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MicrosoftAgentsProcessor: agent run failed");
            await PushErrorAsync($"Agent run failed: {ex.Message}", ex, ct).ConfigureAwait(false);
        }
    }
}
