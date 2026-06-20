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
    public async Task Interim_Then_Final_Fires_The_Agent_Exactly_Once_On_The_Final()
    {
        // VRT-004 T1.3: interims are display/turn-signal only; the agent fires once, on the settled final, and
        // sees only the final text (regression-locks the finals-only match against a future refactor).
        var invocations = 0;
        string? seenText = null;
        var driver = new StubDriver(ctx =>
        {
            Interlocked.Increment(ref invocations);
            seenText = ctx.UserText;
            return Yield(new LlmTextChunkFrame("ok"));
        });
        var (runner, captured, pipeline) = Build(driver);

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new TranscriptionFrame("what's the", IsFinal: false));        // interim
            await pipeline.Source.IngestAsync(new TranscriptionFrame("what's the weather", IsFinal: true)); // final

            await captured.WaitForAsync(f => f is LlmTurnEndedFrame, TimeSpan.FromSeconds(2));
            await Task.Delay(40);

            Assert.Equal(1, invocations);                 // exactly one turn
            Assert.Equal("what's the weather", seenText); // driven by the final text only
        }
    }

    [Fact]
    public async Task Empty_Final_Is_Forwarded_And_A_Later_Real_Final_Still_Runs_A_Turn()
    {
        // VRT-002 WS2 §6.3: an empty/whitespace final must not wedge the pipeline — a subsequent real
        // transcription must still run a turn normally (no anticipatory state latched on a turn that never comes).
        var invocations = 0;
        var driver = new StubDriver(_ => { Interlocked.Increment(ref invocations); return Yield(new LlmTextChunkFrame("ok")); });
        var (runner, captured, pipeline) = Build(driver);

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new TranscriptionFrame("   ", IsFinal: true));   // empty → recovery
            await pipeline.Source.IngestAsync(new TranscriptionFrame("hello", IsFinal: true)); // real → turn

            await captured.WaitForAsync(f => f is LlmTurnEndedFrame, TimeSpan.FromSeconds(2));

            Assert.Equal(1, invocations); // only the real transcription ran a turn
            // The empty final is still forwarded downstream so transports can clear a "listening" affordance.
            Assert.Contains(captured.Captured, f => f is TranscriptionFrame t && string.IsNullOrWhiteSpace(t.Text));
        }
    }

    [Fact]
    public async Task MaxResponseDuration_Truncates_A_Runaway_Turn_But_Still_Ends_It()
    {
        // VRT-002 WS2 §6.5: an unbounded driver stream is truncated at the cap — and the cap is what lets the
        // turn end at all (LlmTurnEndedFrame still fires via the existing finally).
        var driver = new StubDriver(_ => InfiniteChunks());
        var processor = new AgentLoopProcessor(driver, maxResponseDuration: TimeSpan.FromMilliseconds(80));
        var captured = new CapturingProcessor();
        var pipeline = Pipeline.Build()
            .Source(new PipelineSource())
            .Then(processor)
            .Then(captured)
            .Sink(new PipelineSink());

        await using var runner = new PipelineRunner(pipeline);
        await runner.StartAsync();
        await pipeline.Source.IngestAsync(new TranscriptionFrame("go", IsFinal: true));

        await captured.WaitForAsync(f => f is LlmTurnEndedFrame, TimeSpan.FromSeconds(3));

        Assert.Contains(captured.Captured, f => f is LlmTurnEndedFrame);
        var chunks = captured.Captured.OfType<LlmTextChunkFrame>().Count();
        Assert.InRange(chunks, 1, 1000); // some output, then bounded — not the infinite stream
    }

    private static async IAsyncEnumerable<Frame> InfiniteChunks(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        while (true)
        {
            yield return new LlmTextChunkFrame("x");
            await Task.Delay(25, ct);
        }
    }

    /// <summary>A driver that blocks forever without yielding — exercises the cap's cancellation path.</summary>
    private sealed class StallingDriver : IAgentTurnDriver
    {
        public IAsyncEnumerable<Frame> RunTurnAsync(VoiceTurnContext ctx, CancellationToken ct) => Stall(ct);

        private static async IAsyncEnumerable<Frame> Stall([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct); // never yields a frame; only the cap token ends it
            yield break;
        }
    }

    [Fact]
    public async Task MaxResponseDuration_Bounds_A_Driver_That_Stalls_Before_Yielding()
    {
        // VRT-002 WS2 §6.5 (Codex P2): a driver that never yields (a stalled LLM, or one awaiting a tool) must
        // still be bounded — the cap cancels the enumeration itself, not merely a post-yield break.
        var processor = new AgentLoopProcessor(new StallingDriver(), maxResponseDuration: TimeSpan.FromMilliseconds(100));
        var captured = new CapturingProcessor();
        var pipeline = Pipeline.Build()
            .Source(new PipelineSource())
            .Then(processor)
            .Then(captured)
            .Sink(new PipelineSink());

        await using var runner = new PipelineRunner(pipeline);
        await runner.StartAsync();
        await pipeline.Source.IngestAsync(new TranscriptionFrame("go", IsFinal: true));

        await captured.WaitForAsync(f => f is LlmTurnEndedFrame, TimeSpan.FromSeconds(3));
        Assert.Contains(captured.Captured, f => f is LlmTurnEndedFrame); // closed cleanly despite zero output
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

    // ── Disposal (CQ-001) ──────────────────────────────────────────────────

    /// <summary>A driver whose turn blocks forever and records when its token is cancelled.</summary>
    private sealed class CancellationObservingDriver : IAgentTurnDriver
    {
        public readonly TaskCompletionSource TurnStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource TurnCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IAsyncEnumerable<Frame> RunTurnAsync(VoiceTurnContext ctx, CancellationToken ct) => Run(ct);

        private async IAsyncEnumerable<Frame> Run([EnumeratorCancellation] CancellationToken ct)
        {
            TurnStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                TurnCancelled.TrySetResult(); // the turn token fired — the processor CTS was cancelled
                throw;
            }
            yield break;
        }
    }

    [Fact]
    public async Task Disposing_Through_The_Base_Reference_Cancels_The_Turn_Worker_Without_An_EndFrame()
    {
        // CQ-001 regression: PipelineRunner disposes processors through a FrameProcessor-typed reference.
        // The old `new DisposeAsync` (method hiding) was skipped on that base-typed path, leaking the turn
        // worker and its CTS on abrupt teardown. The DisposeAsyncCore hook must run and cancel the worker.
        var driver = new CancellationObservingDriver();
        var (runner, _, pipeline) = Build(driver);

        await runner.StartAsync();
        await pipeline.Source.IngestAsync(new TranscriptionFrame("go", IsFinal: true));
        await driver.TurnStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)); // a turn is actively in flight

        // Abrupt teardown: dispose the runner directly — no EndFrame is ever injected.
        await runner.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        // The in-flight turn must have been cancelled — proof the derived CTS was cancelled via DisposeAsyncCore.
        await driver.TurnCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    /// <summary>A driver that blocks on a frontend-tool result using a token NOT linked to the processor CTS.</summary>
    private sealed class FrontendToolWaitingDriver : IAgentTurnDriver
    {
        public readonly TaskCompletionSource ToolRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IAsyncEnumerable<Frame> RunTurnAsync(VoiceTurnContext ctx, CancellationToken ct) => Run(ctx);

        private async IAsyncEnumerable<Frame> Run(VoiceTurnContext ctx)
        {
            yield return new ToolCallRequestFrame("never-answered", "x", "{}");
            ToolRequested.TrySetResult();
            // CancellationToken.None: only a TCS cancel can release this, not the processor CTS.
            await ctx.FrontendTools.AwaitToolResultAsync("never-answered", CancellationToken.None);
            yield return new LlmTextChunkFrame("unreached");
        }
    }

    [Fact]
    public async Task Disposing_While_A_Driver_Awaits_A_Frontend_Tool_Does_Not_Deadlock()
    {
        // CQ-001 (codex P2): a driver blocked in AwaitToolResultAsync on CancellationToken.None is released
        // only by cancelling its pending TCS. DisposeAsyncCore must cancel pending waits BEFORE awaiting the
        // worker, or PipelineRunner.DisposeAsync hangs forever on the un-released worker.
        var driver = new FrontendToolWaitingDriver();
        var (runner, _, pipeline) = Build(driver);

        await runner.StartAsync();
        await pipeline.Source.IngestAsync(new TranscriptionFrame("go", IsFinal: true));
        await driver.ToolRequested.Task.WaitAsync(TimeSpan.FromSeconds(2)); // worker is now blocked on the TCS

        // A hang here is the deadlock this guards against — must finish well within the timeout.
        await runner.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>A driver stuck on work that never observes the processor token (simulates a buggy/blocking driver).</summary>
    private sealed class UncancellableDriver : IAgentTurnDriver
    {
        public readonly TaskCompletionSource TurnStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IAsyncEnumerable<Frame> RunTurnAsync(VoiceTurnContext ctx, CancellationToken ct) => Run();

        private async IAsyncEnumerable<Frame> Run()
        {
            TurnStarted.TrySetResult();
            await new TaskCompletionSource().Task; // never completes, never observes any cancellation token
            yield break;
        }
    }

    [Fact]
    public async Task Disposing_With_A_Driver_Stuck_In_Uncancellable_Work_Still_Completes()
    {
        // CQ-001 (codex P2, round 2): the worker join must be BOUNDED. A driver stuck in work that never
        // observes the processor token would otherwise make PipelineRunner.DisposeAsync hang forever; the
        // bounded WaitAsync (mirroring OnEndAsync) gives up after the timeout so connection cleanup completes.
        var driver = new UncancellableDriver();
        var (runner, _, pipeline) = Build(driver);

        await runner.StartAsync();
        await pipeline.Source.IngestAsync(new TranscriptionFrame("go", IsFinal: true));
        await driver.TurnStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)); // worker is now stuck, ignoring the CTS

        // Disposal must return despite the stuck worker. Bound is 5 s; allow generous margin for CI.
        await runner.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
    }
}
