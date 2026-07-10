using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Voxa.Frames;
using Voxa.Pipelines;
using Voxa.Processors;
using Voxa.Testing.Processors;

namespace Voxa.Services.MicrosoftAgents.Tests;

/// <summary>
/// VDX-008 WS2 — the MAF side of the talker/thinker split: the injected <c>delegate_task</c> tool
/// and the default message build for background-result turns (the empty-turn regression the spec's
/// §11 calls out explicitly).
/// </summary>
public class BackgroundDelegationTests
{
    private static (ChatClientAgent Agent, FakeChatClient Client) BuildAgent(
        Func<IEnumerable<ChatMessage>, IAsyncEnumerable<ChatResponseUpdate>> stream)
    {
        var client = new FakeChatClient(stream);
        var agent = new ChatClientAgent(client, new ChatClientAgentOptions { Name = "test-agent" });
        return (agent, client);
    }

    private static (PipelineRunner Runner, CapturingProcessor Captured, Pipeline Pipeline, FakeChatClient Client) Build(
        Func<IEnumerable<ChatMessage>, IAsyncEnumerable<ChatResponseUpdate>> stream,
        Action<MicrosoftAgentVoiceOptions>? configure = null)
    {
        var (agent, client) = BuildAgent(stream);
        var processor = MicrosoftAgentVoice.CreateProcessor(agent, configure);
        var captured = new CapturingProcessor();
        var pipeline = Pipeline.Build()
            .Source(new PipelineSource())
            .Then(processor)
            .Then(captured)
            .Sink(new PipelineSink());
        return (new PipelineRunner(pipeline), captured, pipeline, client);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> Updates(params AIContent[] contents)
    {
        foreach (var c in contents)
        {
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = new List<AIContent> { c },
            };
            await Task.Yield();
        }
    }

    // ── Background-result turns through the DEFAULT message build ─────────

    [Fact]
    public async Task Default_BuildMessages_Feeds_ResultText_To_The_Model()
    {
        // The §11 regression test: an empty message (or empty list) here is exactly the
        // UseDefaults()-runs-an-empty-turn bug the spec's WS2 exists to prevent.
        IEnumerable<ChatMessage>? observedMessages = null;
        var (runner, captured, pipeline, _) = Build(messages =>
        {
            observedMessages = messages;
            return Updates(new TextContent("Your flight lands at 6pm."));
        });

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new BackgroundTaskCompletedFrame(
                "task-1", "Flight UA12 lands 18:02 local", ElapsedMs: 5300));

            await captured.WaitForAsync(f => f is LlmTurnEndedFrame, TimeSpan.FromSeconds(10));

            Assert.NotNull(observedMessages);
            var only = Assert.Single(observedMessages!);
            Assert.Equal(ChatRole.User, only.Role);
            Assert.Contains("Flight UA12 lands 18:02 local", only.Text);
            Assert.Contains("respond with NOTHING", only.Text); // the relevance gate travels with the result
            Assert.Contains(captured.Captured, f => f is LlmTextChunkFrame c && c.Text.Contains("6pm"));
        }
    }

    [Fact]
    public async Task Failed_Background_Result_Is_Marked_As_Failed_In_The_Prompt()
    {
        IEnumerable<ChatMessage>? observedMessages = null;
        var (runner, captured, pipeline, _) = Build(messages =>
        {
            observedMessages = messages;
            return Updates(new TextContent("Sorry, that lookup failed."));
        });

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new BackgroundTaskCompletedFrame(
                "task-1", "background task timed out after 120s", IsError: true));

            await captured.WaitForAsync(f => f is LlmTurnEndedFrame, TimeSpan.FromSeconds(10));

            Assert.Contains("FAILED", Assert.Single(observedMessages!).Text, StringComparison.Ordinal);
        }
    }

    // ── delegate_task injection ────────────────────────────────────────────

    [Fact]
    public async Task Delegate_Tool_Is_Injected_On_User_Turns_When_Enabled()
    {
        var (runner, captured, pipeline, client) = Build(
            _ => Updates(new TextContent("on it")),
            opts => opts.EnableBackgroundDelegation = true);

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new TranscriptionFrame("look this up", IsFinal: true));
            await captured.WaitForAsync(f => f is LlmTurnEndedFrame, TimeSpan.FromSeconds(10));

            var tools = client.LastOptions?.Tools;
            Assert.NotNull(tools);
            Assert.Contains(tools!, t => t is AIFunction { Name: "delegate_task" });
        }
    }

    [Fact]
    public async Task Delegate_Tool_Is_Absent_When_Disabled_And_On_Background_Turns()
    {
        var (runner, captured, pipeline, client) = Build(
            _ => Updates(new TextContent("ok")),
            opts => opts.EnableBackgroundDelegation = true);

        await using (runner)
        {
            await runner.StartAsync();

            // Background-result turn: the model must not delegate from a delegation result.
            await pipeline.Source.IngestAsync(new BackgroundTaskCompletedFrame("task-1", "42"));
            await captured.WaitForAsync(f => f is LlmTurnEndedFrame, TimeSpan.FromSeconds(10));
            Assert.DoesNotContain(
                client.LastOptions?.Tools ?? [],
                t => t is AIFunction { Name: "delegate_task" });
        }

        var (runner2, captured2, pipeline2, client2) = Build(_ => Updates(new TextContent("ok")));
        await using (runner2)
        {
            await runner2.StartAsync();
            await pipeline2.Source.IngestAsync(new TranscriptionFrame("hi", IsFinal: true));
            await captured2.WaitForAsync(f => f is LlmTurnEndedFrame, TimeSpan.FromSeconds(10));
            Assert.DoesNotContain(
                client2.LastOptions?.Tools ?? [],
                t => t is AIFunction { Name: "delegate_task" });
        }
    }

    [Fact]
    public async Task Invoking_Delegate_Task_Emits_The_Request_Frame_And_Returns_An_Ack()
    {
        AIFunction? delegateTool = null;
        var (runner, captured, pipeline, client) = Build(
            messages =>
            {
                return Updates(new TextContent("on it"));
            },
            opts => opts.EnableBackgroundDelegation = true);

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new TranscriptionFrame("research VDX-008", IsFinal: true));
            await captured.WaitForAsync(f => f is LlmTurnEndedFrame, TimeSpan.FromSeconds(10));

            delegateTool = client.LastOptions?.Tools?.OfType<AIFunction>()
                .FirstOrDefault(f => f.Name == "delegate_task");
            Assert.NotNull(delegateTool);

            // Invoke the tool exactly as MAF's function-invocation layer would.
            var ack = await delegateTool!.InvokeAsync(new AIFunctionArguments
            {
                ["goal"] = "research VDX-008",
                ["context_summary"] = "user asked about a spec",
            });

            Assert.Contains("Delegated", ack?.ToString(), StringComparison.Ordinal);
            Assert.Contains("do NOT invent", ack?.ToString(), StringComparison.Ordinal);

            await captured.WaitForAsync(f => f is BackgroundTaskRequestFrame, TimeSpan.FromSeconds(2));
            var request = Assert.Single(captured.Captured.OfType<BackgroundTaskRequestFrame>());
            Assert.Equal("research VDX-008", request.Goal);
            Assert.Equal("user asked about a spec", request.ContextJson);
            Assert.False(string.IsNullOrEmpty(request.TaskId));
            Assert.False(string.IsNullOrEmpty(request.OriginTurnId));
        }
    }
}
