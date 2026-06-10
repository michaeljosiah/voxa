using Microsoft.Extensions.AI;

namespace Voxa.Services.MicrosoftAgents;

/// <summary>
/// Bounded per-connection chat history for the default voice pipeline. Single-threaded by
/// construction: the agent processor invokes BuildMessages/OnTurnCompleted from one turn
/// worker at a time, so no locking is needed. Trims the OLDEST messages beyond
/// <see cref="MaxMessages"/> — always in whole user/assistant pairs so the model never
/// sees a conversation that starts with an assistant turn.
/// </summary>
public sealed class InMemoryChatHistory
{
    private readonly List<ChatMessage> _messages = new();

    public int MaxMessages { get; }

    public InMemoryChatHistory(int maxMessages = 50)
    {
        if (maxMessages < 2) throw new ArgumentOutOfRangeException(nameof(maxMessages), "Must be at least 2.");
        MaxMessages = maxMessages;
    }

    public void AddUser(string text)
    {
        _messages.Add(new ChatMessage(ChatRole.User, text));
        Trim();
    }

    public void AddAssistant(string text)
    {
        _messages.Add(new ChatMessage(ChatRole.Assistant, text));
        Trim();
    }

    public IReadOnlyList<ChatMessage> Snapshot() => _messages.ToArray();

    private void Trim()
    {
        // Remove pairs (user + assistant) from the front until we're within the limit.
        while (_messages.Count > MaxMessages && _messages.Count >= 2)
            _messages.RemoveRange(0, 2);

        // Re-align: turns aren't always perfect pairs (an assistant turn can yield empty text,
        // recording only the user message), so blind pair-removal can leave the history starting
        // with an assistant message. Drop leading assistant messages so the model never sees a
        // conversation that opens with the assistant speaking.
        while (_messages.Count > 0 && _messages[0].Role == ChatRole.Assistant)
            _messages.RemoveAt(0);
    }
}
