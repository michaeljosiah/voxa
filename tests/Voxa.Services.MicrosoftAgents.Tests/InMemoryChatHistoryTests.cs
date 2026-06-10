using Microsoft.Extensions.AI;

namespace Voxa.Services.MicrosoftAgents.Tests;

public class InMemoryChatHistoryTests
{
    [Fact]
    public void Trims_Oldest_Pairs_When_Over_Limit()
    {
        var history = new InMemoryChatHistory(maxMessages: 4);

        for (var i = 0; i < 4; i++)
        {
            history.AddUser($"u{i}");
            history.AddAssistant($"a{i}");
        }

        var snapshot = history.Snapshot();
        Assert.Equal(4, snapshot.Count);
        // Oldest pairs trimmed; the newest two turns survive intact.
        Assert.Equal("u2", snapshot[0].Text);
        Assert.Equal("a2", snapshot[1].Text);
        Assert.Equal("u3", snapshot[2].Text);
        Assert.Equal("a3", snapshot[3].Text);
    }

    [Fact]
    public void Never_Starts_With_Assistant_Even_When_Turns_Are_Misaligned()
    {
        var history = new InMemoryChatHistory(maxMessages: 4);

        // A turn whose assistant text was empty records only the user message,
        // shifting pair alignment by one.
        history.AddUser("u0-no-assistant-reply");
        history.AddUser("u1");
        history.AddAssistant("a1");
        history.AddUser("u2");
        history.AddAssistant("a2");
        history.AddUser("u3");
        history.AddAssistant("a3");

        var snapshot = history.Snapshot();
        Assert.True(snapshot.Count <= 4);
        Assert.NotEqual(ChatRole.Assistant, snapshot[0].Role);
    }

    [Fact]
    public void Rejects_MaxMessages_Below_Two()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new InMemoryChatHistory(maxMessages: 1));
    }

    [Fact]
    public void Snapshot_Is_A_Copy_Not_A_Live_View()
    {
        var history = new InMemoryChatHistory();
        history.AddUser("first");

        var snapshot = history.Snapshot();
        history.AddAssistant("second");

        Assert.Single(snapshot);
    }
}
