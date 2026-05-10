using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Voxa.Frames;
using Voxa.Pipelines;
using Voxa.Processors;
using Voxa.Testing.Processors;

namespace Voxa.Services.MicrosoftAgents.Tests;

public class MicrosoftAgentVoiceTests
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
        Func<IEnumerable<ChatMessage>, IAsyncEnumerable<ChatResponseUpdate>> stream,
        Action<MicrosoftAgentVoiceOptions>? configure = null)
    {
        var processor = MicrosoftAgentVoice.CreateProcessor(BuildAgent(stream), configure);
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

    // ── Default-options behavior ───────────────────────────────────────────

    [Fact]
    public async Task Default_Options_Builds_Single_User_Message_And_Streams_Text()
    {
        IEnumerable<ChatMessage>? observedMessages = null;
        var (runner, captured, pipeline) = Build(messages =>
        {
            observedMessages = messages;
            return Updates(new TextContent("Hello, "), new TextContent("world!"));
        });

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new TranscriptionFrame("greet me", IsFinal: true));

            // Wait for the two text chunks plus turn-boundary frames.
            await captured.WaitForAsync(4, TimeSpan.FromSeconds(2));

            var chunks = captured.Captured.OfType<LlmTextChunkFrame>().ToList();
            Assert.Equal(2, chunks.Count);
            Assert.Equal("Hello, ", chunks[0].Text);
            Assert.Equal("world!", chunks[1].Text);

            Assert.NotNull(observedMessages);
            Assert.Single(observedMessages);
            var only = observedMessages!.Single();
            Assert.Equal(ChatRole.User, only.Role);
            Assert.Equal("greet me", only.Text);
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
            Assert.DoesNotContain(captured.Captured, f => f is LlmTurnStartedFrame);
        }
    }

    [Fact]
    public async Task Turn_Started_And_Ended_Frames_Bracket_Each_Turn()
    {
        var (runner, captured, pipeline) = Build(_ => Updates(new TextContent("ok")));

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new TranscriptionFrame("hi", IsFinal: true));

            await captured.WaitForAsync(3, TimeSpan.FromSeconds(2));

            var ordered = captured.Captured.ToList();
            var startIdx = ordered.FindIndex(f => f is LlmTurnStartedFrame);
            var endIdx = ordered.FindIndex(f => f is LlmTurnEndedFrame);
            var chunkIdx = ordered.FindIndex(f => f is LlmTextChunkFrame);

            Assert.True(startIdx >= 0, "LlmTurnStartedFrame must be emitted.");
            Assert.True(endIdx >= 0, "LlmTurnEndedFrame must be emitted.");
            Assert.True(startIdx < chunkIdx, "Started frame must precede text chunks.");
            Assert.True(chunkIdx < endIdx, "Ended frame must follow text chunks.");

            // TurnId pairing — Started and Ended must agree.
            var started = (LlmTurnStartedFrame)ordered[startIdx];
            var ended = (LlmTurnEndedFrame)ordered[endIdx];
            Assert.Equal(started.TurnId, ended.TurnId);
        }
    }

    // ── BuildMessages hook ────────────────────────────────────────────────

    [Fact]
    public async Task BuildMessages_Override_Replaces_Default_Single_Message_Form()
    {
        IEnumerable<ChatMessage>? observed = null;
        var (runner, captured, pipeline) = Build(
            messages =>
            {
                observed = messages;
                return Updates(new TextContent("ok"));
            },
            options =>
            {
                options.BuildMessages = (turn, ct) => ValueTask.FromResult<IReadOnlyList<ChatMessage>>(
                    new ChatMessage[]
                    {
                        new(ChatRole.System, "You are testy."),
                        new(ChatRole.User, "previous turn"),
                        new(ChatRole.Assistant, "previous reply"),
                        new(ChatRole.User, turn.UserText),
                    });
            });

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new TranscriptionFrame("current turn", IsFinal: true));
            await captured.WaitForAsync(2, TimeSpan.FromSeconds(2));

            Assert.NotNull(observed);
            var list = observed!.ToList();
            Assert.Equal(4, list.Count);
            Assert.Equal(ChatRole.System, list[0].Role);
            Assert.Equal("current turn", list[3].Text);
        }
    }

    // ── Frontend tool round-trip ──────────────────────────────────────────

    [Fact]
    public async Task Frontend_Tool_Call_Round_Trips_Through_Pipeline_Source()
    {
        var runStreaming = new[]
        {
            // First invocation: emit a frontend tool call.
            (Func<IEnumerable<ChatMessage>, IAsyncEnumerable<ChatResponseUpdate>>)(_ => Updates(
                new FunctionCallContent("call_abc", "display_chart",
                    new Dictionary<string, object?> { ["data"] = "[1,2,3]" }))),
            // Second invocation (after we feed the result back): emit a final text chunk.
            (Func<IEnumerable<ChatMessage>, IAsyncEnumerable<ChatResponseUpdate>>)(_ => Updates(
                new TextContent("Done."))),
        };
        var invocation = 0;

        var (runner, captured, pipeline) = Build(
            messages => runStreaming[Math.Min(invocation++, runStreaming.Length - 1)](messages),
            options =>
            {
                options.IsFrontendTool = name => name == "display_chart";
            });

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new TranscriptionFrame("chart please", IsFinal: true));

            // Wait until we see the ToolCallRequestFrame downstream.
            await captured.WaitForAsync(
                f => f is ToolCallRequestFrame,
                TimeSpan.FromSeconds(2));

            // Simulate the client returning a result. Voxa's data loop should pick it up,
            // complete the TCS, and the driver should re-invoke the agent.
            await pipeline.Source.IngestAsync(new ToolCallResultFrame("call_abc", "{\"ok\":true}"));

            // Now we expect the second invocation's text chunk to appear.
            await captured.WaitForAsync(
                f => f is LlmTextChunkFrame chunk && chunk.Text == "Done.",
                TimeSpan.FromSeconds(2));

            // The agent has been invoked at least twice: once to emit the tool call, then again
            // (after the tool result resolved) to emit the final text. MAF's ChatClientAgent may
            // make additional internal passes over a `FunctionCallContent` it observes — we don't
            // assert an exact count, just that the re-run path actually exercised both invocations.
            Assert.True(invocation >= 2,
                $"Expected at least 2 agent invocations (initial + re-run); got {invocation}.");
        }
    }

    [Fact]
    public async Task Backend_Tool_Calls_Are_Not_Round_Tripped()
    {
        var (runner, captured, pipeline) = Build(
            _ => Updates(
                new FunctionCallContent("call_xyz", "pf_get_invoices", new Dictionary<string, object?>()),
                new TextContent("done")),
            options =>
            {
                options.IsFrontendTool = _ => false; // everything is backend
            });

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new TranscriptionFrame("invoices", IsFinal: true));

            await captured.WaitForAsync(
                f => f is LlmTextChunkFrame chunk && chunk.Text == "done",
                TimeSpan.FromSeconds(2));

            // No ToolCallRequestFrame should appear — backend tools don't round-trip.
            Assert.DoesNotContain(captured.Captured, f => f is ToolCallRequestFrame);
            // Also: no leak of raw FunctionCallContent through any frame type.
            Assert.DoesNotContain(captured.Captured, f => f is StatusFrame);
        }
    }

    [Fact]
    public async Task Backend_Tool_Calls_Yield_StatusFrame_When_BuildBackendToolStatus_Returns_Message()
    {
        // Backend tool progress pattern: the agent emits an acknowledgement TextContent,
        // then calls a backend tool. Voxa must surface a sanitized status while MAF executes
        // the tool — exposing the raw tool name to consumer UI is forbidden.
        //
        // Note: with no AIFunction registered on the agent, MAF re-invokes the chat client whenever
        // it sees an unresolved FunctionCallContent. The fake here returns the same content on each
        // call, so the loop emits multiple StatusFrames over time — we only assert that AT LEAST
        // one arrives quickly with the expected sanitized message and no raw tool name leak.
        var (runner, captured, pipeline) = Build(
            _ => Updates(
                new TextContent("Yep, give me a moment."),
                new FunctionCallContent("call_1", "pf_get_spending_summary", new Dictionary<string, object?>())),
            options =>
            {
                options.IsFrontendTool = _ => false; // backend
                options.BuildBackendToolStatus = name => name switch
                {
                    "pf_get_spending_summary" => "Checking your spending...",
                    _ => null,
                };
            });

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new TranscriptionFrame("top expenses", IsFinal: true));
            await captured.WaitForAsync(f => f is StatusFrame, TimeSpan.FromSeconds(2));

            var statuses = captured.Captured.OfType<StatusFrame>().ToList();
            Assert.NotEmpty(statuses);
            Assert.Equal("Checking your spending...", statuses[0].Message);
            // No raw tool name leakage in any status frame.
            Assert.DoesNotContain(
                statuses,
                f => f.Message.Contains("pf_", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Backend_Tool_Status_Suppressed_When_BuildBackendToolStatus_Returns_Null()
    {
        // Hosts opt out per-tool by returning null. Default (delegate not configured at all) also
        // suppresses status — ensures backwards compatibility with hosts that haven't adopted the
        // backend-tool progress pattern.
        //
        // Wait for the acknowledgement text instead of LlmTurnEndedFrame because the unresolved
        // FunctionCallContent keeps MAF in its tool-retry loop — the turn never naturally ends.
        var (runner, captured, pipeline) = Build(
            _ => Updates(
                new TextContent("ack"),
                new FunctionCallContent("call_1", "internal_telemetry_ping", new Dictionary<string, object?>())),
            options =>
            {
                options.IsFrontendTool = _ => false;
                options.BuildBackendToolStatus = _ => null;
            });

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new TranscriptionFrame("ping", IsFinal: true));
            await captured.WaitForAsync(
                f => f is LlmTextChunkFrame chunk && chunk.Text == "ack",
                TimeSpan.FromSeconds(2));

            Assert.DoesNotContain(captured.Captured, f => f is StatusFrame);
        }
    }

    [Fact]
    public async Task Backend_Tool_Acknowledgement_Text_Reaches_Pipeline_Before_StatusFrame()
    {
        // Critical ordering for the conversational pattern: the acknowledgement text must reach
        // downstream (and be available for TTS) BEFORE the status frame, otherwise the user hears
        // silence followed by both at once.
        var (runner, captured, pipeline) = Build(
            _ => Updates(
                new TextContent("Yep, give me a moment."),
                new FunctionCallContent("call_1", "pf_get_spending_summary", new Dictionary<string, object?>())),
            options =>
            {
                options.IsFrontendTool = _ => false;
                options.BuildBackendToolStatus = _ => "Checking your spending...";
            });

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new TranscriptionFrame("top expenses", IsFinal: true));
            await captured.WaitForAsync(f => f is StatusFrame, TimeSpan.FromSeconds(2));

            var ordered = captured.Captured.ToList();
            var ackIdx = ordered.FindIndex(f => f is LlmTextChunkFrame chunk && chunk.Text == "Yep, give me a moment.");
            var statusIdx = ordered.FindIndex(f => f is StatusFrame);

            Assert.True(ackIdx >= 0, "Acknowledgement text chunk must reach the captured stream.");
            Assert.True(statusIdx >= 0, "Status frame must reach the captured stream.");
            Assert.True(ackIdx < statusIdx,
                "Acknowledgement text must be downstream BEFORE the backend-tool status frame.");
        }
    }

    // ── Lifecycle hooks ────────────────────────────────────────────────────

    [Fact]
    public async Task OnTurnStarted_And_OnTurnCompleted_Fire_With_Matching_TurnId()
    {
        string? startedTurnId = null;
        string? completedTurnId = null;
        TurnSummary? capturedSummary = null;

        var (runner, captured, pipeline) = Build(
            _ => Updates(new TextContent("one"), new TextContent(" two")),
            options =>
            {
                options.OnTurnStarted = (turn, ct) => { startedTurnId = turn.TurnId; return ValueTask.CompletedTask; };
                options.OnTurnCompleted = (turn, summary, ct) =>
                {
                    completedTurnId = turn.TurnId;
                    capturedSummary = summary;
                    return ValueTask.CompletedTask;
                };
            });

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new TranscriptionFrame("hi", IsFinal: true));
            await captured.WaitForAsync(f => f is LlmTurnEndedFrame, TimeSpan.FromSeconds(2));

            Assert.NotNull(startedTurnId);
            Assert.NotNull(completedTurnId);
            Assert.Equal(startedTurnId, completedTurnId);
            Assert.NotNull(capturedSummary);
            Assert.Equal("one two", capturedSummary!.AssistantText);
            Assert.Equal(startedTurnId, capturedSummary.TurnId);
        }
    }

    [Fact]
    public async Task OnTurnFailed_Fires_When_Driver_Throws_And_Worker_Drains_Next_Turn()
    {
        var invocation = 0;
        var failureSeen = false;

        var (runner, captured, pipeline) = Build(
            _ =>
            {
                invocation++;
                if (invocation == 1)
                {
                    return ThrowingUpdates();
                }
                return Updates(new TextContent("recovered"));
            },
            options =>
            {
                options.OnTurnFailed = (turn, ex, ct) => { failureSeen = true; return ValueTask.CompletedTask; };
            });

        await using (runner)
        {
            await runner.StartAsync();

            // First turn — driver throws.
            await pipeline.Source.IngestAsync(new TranscriptionFrame("first", IsFinal: true));
            await Task.Delay(150);

            Assert.True(failureSeen);

            // Second turn — worker should still be alive and draining.
            await pipeline.Source.IngestAsync(new TranscriptionFrame("second", IsFinal: true));
            await captured.WaitForAsync(
                f => f is LlmTextChunkFrame chunk && chunk.Text == "recovered",
                TimeSpan.FromSeconds(2));
        }

#pragma warning disable CS1998, CS0162
        static async IAsyncEnumerable<ChatResponseUpdate> ThrowingUpdates()
        {
            throw new InvalidOperationException("synthetic-failure");
            yield break;
        }
#pragma warning restore CS1998, CS0162
    }

    // ── Transcription forwarding (UI ordering) ─────────────────────────────

    [Fact]
    public async Task TranscriptionFrame_Is_Forwarded_Before_LlmTextChunkFrame()
    {
        // UI rendering depends on this: the user's transcription bubble must appear before the
        // assistant's reply text starts streaming in.
        var (runner, captured, pipeline) = Build(_ => Updates(new TextContent("Sure, "), new TextContent("here you go.")));

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new TranscriptionFrame("what's up?", IsFinal: true));
            await captured.WaitForAsync(f => f is LlmTurnEndedFrame, TimeSpan.FromSeconds(2));

            var ordered = captured.Captured.ToList();

            int IndexOf(Func<Frame, bool> p)
            {
                for (int i = 0; i < ordered.Count; i++)
                    if (p(ordered[i])) return i;
                return -1;
            }

            var transcriptionIdx = IndexOf(f => f is TranscriptionFrame t && t.IsFinal && t.Text == "what's up?");
            var firstChunkIdx = IndexOf(f => f is LlmTextChunkFrame);

            Assert.True(transcriptionIdx >= 0);
            Assert.True(firstChunkIdx >= 0);
            Assert.True(transcriptionIdx < firstChunkIdx,
                "TranscriptionFrame must arrive BEFORE the first LlmTextChunkFrame.");
        }
    }
}
