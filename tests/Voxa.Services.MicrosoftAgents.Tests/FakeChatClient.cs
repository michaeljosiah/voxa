using Microsoft.Extensions.AI;

namespace Voxa.Services.MicrosoftAgents.Tests;

/// <summary>
/// In-memory <see cref="IChatClient"/> for tests. The streaming handler is provided as a delegate so
/// each test scripts its own response sequence.
/// </summary>
internal sealed class FakeChatClient : IChatClient
{
    private readonly Func<IEnumerable<ChatMessage>, IAsyncEnumerable<ChatResponseUpdate>> _stream;

    public FakeChatClient(Func<IEnumerable<ChatMessage>, IAsyncEnumerable<ChatResponseUpdate>> stream)
    {
        _stream = stream;
    }

    /// <summary>The ChatOptions the agent passed on the most recent streaming call (VDX-008 tool-injection asserts).</summary>
    public ChatOptions? LastOptions { get; private set; }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        LastOptions = options;
        return _stream(messages);
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("FakeChatClient supports streaming only.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
