using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Voxa.Frames;
using Voxa.Pipelines;
using Voxa.Processors;
using Voxa.Testing.Processors;

namespace Voxa.Services.MicrosoftAgents.Tests;

public class MicrosoftAgentsProcessorTests
{
    private static ChatClientAgent BuildAgent(Func<IEnumerable<ChatMessage>, IAsyncEnumerable<ChatResponseUpdate>> stream)
    {
        var client = new FakeChatClient(stream);
        return new ChatClientAgent(client, new ChatClientAgentOptions
        {
            Name = "test-agent",
        });
    }

    private static (PipelineRunner Runner, CapturingProcessor Captured, Pipeline Pipeline) Build(
        Func<IEnumerable<ChatMessage>, IAsyncEnumerable<ChatResponseUpdate>> stream)
    {
        var processor = new MicrosoftAgentsProcessor(BuildAgent(stream));
        var captured = new CapturingProcessor();
        var pipeline = Pipeline.Build()
            .Source(new PipelineSource())
            .Then(processor)
            .Then(captured)
            .Sink(new PipelineSink());
        return (new PipelineRunner(pipeline), captured, pipeline);
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

    [Fact]
    public async Task Final_Transcription_Triggers_Agent_And_Emits_LlmTextChunkFrames()
    {
        var (runner, captured, pipeline) = Build(_ => Updates(
            new TextContent("Hello, "),
            new TextContent("world!")));

        await using (runner)
        {
            await runner.StartAsync();

            await pipeline.Source.IngestAsync(new TranscriptionFrame("greet me", IsFinal: true));

            await captured.WaitForAsync(3, TimeSpan.FromSeconds(2));
            var chunks = captured.Captured.OfType<LlmTextChunkFrame>().ToList();
            Assert.Equal(2, chunks.Count);
            Assert.Equal("Hello, ", chunks[0].Text);
            Assert.Equal("world!", chunks[1].Text);
        }
    }

    [Fact]
    public async Task Interim_Transcription_Does_Not_Run_Agent()
    {
        var ran = false;
        var (runner, captured, pipeline) = Build(_ =>
        {
            ran = true;
            return Updates(new TextContent("hi"));
        });

        await using (runner)
        {
            await runner.StartAsync();

            await pipeline.Source.IngestAsync(new TranscriptionFrame("partial", IsFinal: false));
            await Task.Delay(80);

            Assert.False(ran);
            Assert.DoesNotContain(captured.Captured, f => f is LlmTextChunkFrame);
        }
    }

    [Fact]
    public async Task FunctionCallContent_Becomes_ToolCallRequestFrame()
    {
        var (runner, captured, pipeline) = Build(_ => Updates(
            new FunctionCallContent("call_abc", "get_weather",
                new Dictionary<string, object?> { ["city"] = "Lagos" })));

        await using (runner)
        {
            await runner.StartAsync();

            await pipeline.Source.IngestAsync(new TextFrame("what's the weather?"));

            await captured.WaitForAsync(2, TimeSpan.FromSeconds(2));
            var call = captured.Captured.OfType<ToolCallRequestFrame>().FirstOrDefault();
            Assert.NotNull(call);
            Assert.Equal("call_abc", call!.CallId);
            Assert.Equal("get_weather", call.Name);
            Assert.Contains("Lagos", call.ArgumentsJson);
        }
    }

    [Fact]
    public async Task TranscriptionFrame_Is_Forwarded_Before_First_LlmTextChunk()
    {
        // UI rendering depends on this: the user's transcription bubble must appear before
        // the assistant's reply text starts streaming in. Otherwise the chat shows the bot
        // talking before the user even said anything.
        var (runner, captured, pipeline) = Build(_ => Updates(
            new TextContent("Sure, "),
            new TextContent("here you go.")));

        await using (runner)
        {
            await runner.StartAsync();

            await pipeline.Source.IngestAsync(new TranscriptionFrame("what's up?", IsFinal: true));

            await captured.WaitForAsync(3, TimeSpan.FromSeconds(2));

            int IndexOf(Func<Frame, bool> p)
            {
                for (int i = 0; i < captured.Captured.Count; i++)
                    if (p(captured.Captured[i])) return i;
                return -1;
            }

            var transcriptionIndex = IndexOf(f => f is TranscriptionFrame t && t.IsFinal && t.Text == "what's up?");
            var firstChunkIndex = IndexOf(f => f is LlmTextChunkFrame);

            Assert.True(transcriptionIndex >= 0, "TranscriptionFrame must reach the sink.");
            Assert.True(firstChunkIndex >= 0, "LLM text chunks must reach the sink.");
            Assert.True(transcriptionIndex < firstChunkIndex,
                "TranscriptionFrame must arrive BEFORE the first LlmTextChunkFrame so the user bubble renders before the bot reply.");
        }
    }

    [Fact]
    public async Task Agent_Exception_Surfaces_As_PipelineFailedException()
    {
        var (runner, _, pipeline) = Build(_ => Throws());

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new TextFrame("trigger"));

            var ex = await Assert.ThrowsAsync<PipelineFailedException>(
                async () => await runner.WaitAsync().WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Contains("kaboom", ex.Message);
        }

#pragma warning disable CS1998, CS0162
        static async IAsyncEnumerable<ChatResponseUpdate> Throws()
        {
            throw new InvalidOperationException("kaboom");
            yield break;
        }
#pragma warning restore CS1998, CS0162
    }
}
