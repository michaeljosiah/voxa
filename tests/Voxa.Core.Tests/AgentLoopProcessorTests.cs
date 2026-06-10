using System.Diagnostics;
using System.Runtime.CompilerServices;
using Voxa.Frames;
using Voxa.Pipelines;
using Voxa.Processors;
using Voxa.Testing.Processors;

namespace Voxa.Core.Tests;

public class AgentLoopProcessorTests
{
    /// <summary>Test driver that yields a fixed sequence of frames per turn.</summary>
    private sealed class StubDriver : IAgentTurnDriver
    {
        private readonly Func<VoiceTurnContext, IAsyncEnumerable<Frame>> _impl;

        public StubDriver(Func<VoiceTurnContext, IAsyncEnumerable<Frame>> impl) => _impl = impl;

        public IAsyncEnumerable<Frame> RunTurnAsync(VoiceTurnContext ctx, CancellationToken ct) => _impl(ctx);
    }

    private static (PipelineRunner Runner, CapturingProcessor Captured, Pipeline Pipeline) Build(
        IAgentTurnDriver driver,
        Func<VoiceTurnContext, CancellationToken, ValueTask>? onTurnStarted = null,
        Func<VoiceTurnContext, TurnSummary, CancellationToken, ValueTask>? onTurnCompleted = null,
        Func<VoiceTurnContext, Exception, CancellationToken, ValueTask>? onTurnFailed = null,
        Func<VoiceTurnContext>? contextFactory = null)
    {
        var processor = new AgentLoopProcessor(driver, onTurnStarted, onTurnCompleted, onTurnFailed, contextFactory);
        var captured = new CapturingProcessor();
        var pipeline = Pipeline.Build()
            .Source(new PipelineSource())
            .Then(processor)
            .Then(captured)
            .Sink(new PipelineSink());
        return (new PipelineRunner(pipeline), captured, pipeline);
    }

    private static async IAsyncEnumerable<Frame> Yield(params Frame[] frames)
    {
        foreach (var f in frames)
        {
            yield return f;
            await Task.Yield();
        }
    }

    /// <summary>
    /// Poll for a condition set by a turn-worker hook instead of a fixed delay — fixed delays
    /// flake on slow CI runners where the worker hasn't run within the budget.
    /// </summary>
    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
    }

    // ── Turn lifecycle ────────────────────────────────────────────────────

    [Fact]
    public async Task TranscriptionFrame_Triggers_Turn_With_LlmTurnStarted_And_Ended()
    {
        var driver = new StubDriver(_ => Yield(new LlmTextChunkFrame("hi")));
        var (runner, captured, pipeline) = Build(driver);

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new TranscriptionFrame("hello", IsFinal: true));

            await captured.WaitForAsync(f => f is LlmTurnEndedFrame, TimeSpan.FromSeconds(2));

            var ordered = captured.Captured.ToList();
            var startIdx = ordered.FindIndex(f => f is LlmTurnStartedFrame);
            var endIdx = ordered.FindIndex(f => f is LlmTurnEndedFrame);
            Assert.True(startIdx >= 0 && endIdx > startIdx);

            var started = (LlmTurnStartedFrame)ordered[startIdx];
            var ended = (LlmTurnEndedFrame)ordered[endIdx];
            Assert.Equal(started.TurnId, ended.TurnId);
            Assert.False(string.IsNullOrEmpty(started.TurnId));
        }
    }

    [Fact]
    public async Task Interim_Or_Empty_Transcription_Does_Not_Trigger_A_Turn()
    {
        var driverInvocations = 0;
        var driver = new StubDriver(_ => { driverInvocations++; return Yield(new LlmTextChunkFrame("ignored")); });
        var (runner, captured, pipeline) = Build(driver);

        await using (runner)
        {
            await runner.StartAsync();

            await pipeline.Source.IngestAsync(new TranscriptionFrame("partial", IsFinal: false));
            await pipeline.Source.IngestAsync(new TranscriptionFrame("", IsFinal: true));
            await pipeline.Source.IngestAsync(new TranscriptionFrame("   ", IsFinal: true));
            await Task.Delay(80);

            Assert.Equal(0, driverInvocations);
            Assert.DoesNotContain(captured.Captured, f => f is LlmTurnStartedFrame);
        }
    }

    [Fact]
    public async Task TranscriptionFrame_Is_Forwarded_Downstream_Before_Driver_Yields_Anything()
    {
        // UI ordering: user transcript bubble must render before bot text starts streaming.
        var driver = new StubDriver(_ => Yield(new LlmTextChunkFrame("hi")));
        var (runner, captured, pipeline) = Build(driver);

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new TranscriptionFrame("hello", IsFinal: true));
            await captured.WaitForAsync(f => f is LlmTurnEndedFrame, TimeSpan.FromSeconds(2));

            var ordered = captured.Captured.ToList();
            var transcriptIdx = ordered.FindIndex(f => f is TranscriptionFrame);
            var firstChunkIdx = ordered.FindIndex(f => f is LlmTextChunkFrame);

            Assert.True(transcriptIdx >= 0);
            Assert.True(firstChunkIdx > transcriptIdx);
        }
    }

    // ── Frontend tool round-trip ──────────────────────────────────────────

    [Fact]
    public async Task Frontend_Tool_Awaited_Result_Resolves_From_ToolCallResultFrame_On_Source()
    {
        // Driver yields a ToolCallRequestFrame, awaits via gateway, then yields a final text chunk.
        var driver = new StubDriver(DriveFrontend);
        var (runner, captured, pipeline) = Build(driver);

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new TranscriptionFrame("show me", IsFinal: true));

            await captured.WaitForAsync(f => f is ToolCallRequestFrame, TimeSpan.FromSeconds(2));

            // Send the result back through the source — the data loop should complete the TCS.
            await pipeline.Source.IngestAsync(new ToolCallResultFrame("call_xyz", "{\"ok\":true}"));

            await captured.WaitForAsync(
                f => f is LlmTextChunkFrame chunk && chunk.Text == "after-result",
                TimeSpan.FromSeconds(2));
        }
    }

    private static async IAsyncEnumerable<Frame> DriveFrontend(VoiceTurnContext ctx)
    {
        yield return new ToolCallRequestFrame("call_xyz", "test_tool", "{}");
        var result = await ctx.FrontendTools.AwaitToolResultAsync("call_xyz", CancellationToken.None);
        Assert.Equal("call_xyz", result.CallId);
        Assert.Equal("{\"ok\":true}", result.ResultJson);
        yield return new LlmTextChunkFrame("after-result");
    }

    [Fact]
    public async Task ToolCallResultFrame_Without_Pending_CallId_Is_Silently_Dropped()
    {
        var driver = new StubDriver(_ => Yield(new LlmTextChunkFrame("hi")));
        var (runner, captured, pipeline) = Build(driver);

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new ToolCallResultFrame("nobody-asked", "{}"));
            await Task.Delay(50);

            // The data loop should not propagate the result downstream when no driver is awaiting.
            Assert.DoesNotContain(captured.Captured, f => f is ToolCallResultFrame);
        }
    }

    [Fact]
    public async Task Duplicate_Frontend_CallId_Throws_On_Second_Await()
    {
        var driver = new StubDriver(DriveDuplicate);
        Exception? captured = null;

        var (runner, _, pipeline) = Build(
            driver,
            onTurnFailed: (_, ex, _) => { captured = ex; return ValueTask.CompletedTask; });

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new TranscriptionFrame("dup", IsFinal: true));
            await WaitForConditionAsync(() => captured is not null, TimeSpan.FromSeconds(5));

            Assert.NotNull(captured);
            Assert.IsType<InvalidOperationException>(captured);
            Assert.Contains("Duplicate frontend-tool callId", captured!.Message);
        }
    }

    private static async IAsyncEnumerable<Frame> DriveDuplicate(VoiceTurnContext ctx)
    {
        yield return new ToolCallRequestFrame("dup", "x", "{}");
        // First await — registers and (intentionally) doesn't receive a result; we then re-await
        // the same callId to trigger the duplicate-id guard.
        _ = ctx.FrontendTools.AwaitToolResultAsync("dup", CancellationToken.None);
        await ctx.FrontendTools.AwaitToolResultAsync("dup", CancellationToken.None);
        yield return new LlmTextChunkFrame("unreached");
    }

    // ── Deadlock-safety guarantees ────────────────────────────────────────

    [Fact]
    public async Task ProcessFrameAsync_Returns_Quickly_Even_When_Driver_Is_Waiting_On_Tool_Result()
    {
        // The data loop must keep flowing while the driver awaits a frontend tool result.
        // We drive the agent into a frontend-tool wait, then send a stream of unrelated frames and
        // verify they pass through (proving the data loop isn't blocked).
        var driver = new StubDriver(DriveAndWait);
        var (runner, captured, pipeline) = Build(driver);

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new TranscriptionFrame("trigger", IsFinal: true));

            // Wait for the frontend tool request to land — proves the driver is now blocked on a TCS.
            await captured.WaitForAsync(f => f is ToolCallRequestFrame, TimeSpan.FromSeconds(2));

            // Send an unrelated AudioRawFrame — should pass through immediately.
            var unrelated = new AudioRawFrame(new ReadOnlyMemory<byte>(new byte[] { 1, 2, 3 }), 16000, 1);
            var sw = Stopwatch.StartNew();
            await pipeline.Source.IngestAsync(unrelated);
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 100, $"Source ingest blocked for {sw.ElapsedMilliseconds}ms");

            await captured.WaitForAsync(f => f is AudioRawFrame, TimeSpan.FromSeconds(1));
            Assert.Contains(captured.Captured, f => f is AudioRawFrame);

            // Now release the tool wait so the runner can shut down cleanly.
            await pipeline.Source.IngestAsync(new ToolCallResultFrame("blocking-call", "{}"));
            await captured.WaitForAsync(f => f is LlmTurnEndedFrame, TimeSpan.FromSeconds(2));
        }
    }

    private static async IAsyncEnumerable<Frame> DriveAndWait(VoiceTurnContext ctx)
    {
        yield return new ToolCallRequestFrame("blocking-call", "x", "{}");
        await ctx.FrontendTools.AwaitToolResultAsync("blocking-call", CancellationToken.None);
        yield return new LlmTextChunkFrame("done");
    }

    // ── Per-turn isolation ────────────────────────────────────────────────

    [Fact]
    public async Task Driver_Throws_On_First_Turn_But_Worker_Drains_Second_Turn()
    {
        var invocation = 0;
        var driver = new StubDriver(_ =>
        {
            invocation++;
            if (invocation == 1) return ThrowingFrames();
            return Yield(new LlmTextChunkFrame("recovered"));
        });

        var (runner, captured, pipeline) = Build(driver);

        await using (runner)
        {
            await runner.StartAsync();

            await pipeline.Source.IngestAsync(new TranscriptionFrame("first", IsFinal: true));
            await Task.Delay(80);

            // ErrorFrame is upstream-direction, so it doesn't show up in the downstream-captured
            // list. We just check that the second turn still runs.
            await pipeline.Source.IngestAsync(new TranscriptionFrame("second", IsFinal: true));
            await captured.WaitForAsync(
                f => f is LlmTextChunkFrame chunk && chunk.Text == "recovered",
                TimeSpan.FromSeconds(2));
        }

#pragma warning disable CS1998, CS0162
        static async IAsyncEnumerable<Frame> ThrowingFrames()
        {
            throw new InvalidOperationException("synthetic-failure");
            yield break;
        }
#pragma warning restore CS1998, CS0162
    }

    [Fact]
    public async Task OnTurnFailed_Hook_Receives_Exception()
    {
        Exception? observed = null;

        var driver = new StubDriver(_ => ThrowingFrames());
        var (runner, _, pipeline) = Build(
            driver,
            onTurnFailed: (_, ex, _) => { observed = ex; return ValueTask.CompletedTask; });

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new TranscriptionFrame("boom", IsFinal: true));
            await WaitForConditionAsync(() => observed is not null, TimeSpan.FromSeconds(5));

            Assert.NotNull(observed);
            Assert.IsType<InvalidOperationException>(observed);
            Assert.Equal("synthetic-failure", observed!.Message);
        }

#pragma warning disable CS1998, CS0162
        static async IAsyncEnumerable<Frame> ThrowingFrames()
        {
            throw new InvalidOperationException("synthetic-failure");
            yield break;
        }
#pragma warning restore CS1998, CS0162
    }

    // ── Lifecycle hooks ────────────────────────────────────────────────────

    [Fact]
    public async Task OnTurnStarted_Fires_Before_Driver_Runs_And_OnTurnCompleted_Receives_Summary()
    {
        var startedTurnId = (string?)null;
        var completedSummary = (TurnSummary?)null;
        var driverInvokedAfterStarted = false;

        var driver = new StubDriver(_ =>
        {
            driverInvokedAfterStarted = startedTurnId is not null;
            return Yield(new LlmTextChunkFrame("alpha "), new LlmTextChunkFrame("beta"));
        });

        var (runner, captured, pipeline) = Build(
            driver,
            onTurnStarted: (turn, _) => { startedTurnId = turn.TurnId; return ValueTask.CompletedTask; },
            onTurnCompleted: (_, summary, _) => { completedSummary = summary; return ValueTask.CompletedTask; });

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new TranscriptionFrame("hi", IsFinal: true));
            await captured.WaitForAsync(f => f is LlmTurnEndedFrame, TimeSpan.FromSeconds(2));

            Assert.True(driverInvokedAfterStarted, "Driver should run AFTER onTurnStarted has set the id.");
            Assert.NotNull(completedSummary);
            Assert.Equal("alpha beta", completedSummary!.AssistantText);
            Assert.Equal(startedTurnId, completedSummary.TurnId);
            Assert.True(completedSummary.ElapsedMs >= 0);
        }
    }

    // ── Custom context factory (host metadata seeding) ────────────────────

    [Fact]
    public async Task Context_Factory_Pre_Populates_Metadata_Bag()
    {
        IDictionary<string, object?>? observed = null;

        var driver = new StubDriver(ctx =>
        {
            observed = ctx.Metadata;
            return Yield(new LlmTextChunkFrame("x"));
        });

        var (runner, captured, pipeline) = Build(
            driver,
            contextFactory: () => new VoiceTurnContext
            {
                TurnId = "ignored", // overwritten by AgentLoopProcessor
                UserText = "ignored",
                FrontendTools = null!,
                Emitter = null!,
                Metadata = new Dictionary<string, object?>
                {
                    ["host.thing"] = "value-from-host",
                },
            });

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new TranscriptionFrame("hi", IsFinal: true));
            await captured.WaitForAsync(f => f is LlmTurnEndedFrame, TimeSpan.FromSeconds(2));

            Assert.NotNull(observed);
            Assert.Equal("value-from-host", observed!["host.thing"]);
        }
    }

    // ── Out-of-band frame emission ────────────────────────────────────────

    [Fact]
    public async Task Driver_Can_Push_Custom_Frames_Via_Emitter()
    {
        // Custom frame pushed via the emitter (out-of-band) BEFORE the iterator yields its first
        // standard frame.
        var driver = new StubDriver(EmitAndYield);

        var (runner, captured, pipeline) = Build(driver);

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new TranscriptionFrame("hi", IsFinal: true));
            await captured.WaitForAsync(f => f is LlmTurnEndedFrame, TimeSpan.FromSeconds(2));

            Assert.Contains(captured.Captured, f => f is TextFrame t && t.Text == "custom-from-emitter");
            Assert.Contains(captured.Captured, f => f is LlmTextChunkFrame c && c.Text == "yielded");
        }
    }

    private static async IAsyncEnumerable<Frame> EmitAndYield(VoiceTurnContext ctx)
    {
        await ctx.Emitter.EmitAsync(new TextFrame("custom-from-emitter"), CancellationToken.None);
        yield return new LlmTextChunkFrame("yielded");
    }
}
