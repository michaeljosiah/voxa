using System.Runtime.CompilerServices;
using Voxa.Frames;
using Voxa.Pipelines;
using Voxa.Processors;
using Voxa.Testing.Processors;

namespace Voxa.Core.Tests;

public class BackgroundAgentProcessorTests
{
    /// <summary>Driver whose per-task behavior the test scripts; signals entry so tests can sequence deterministically.</summary>
    private sealed class ScriptedDriver : IAgentTurnDriver
    {
        private readonly Func<VoiceTurnContext, CancellationToken, IAsyncEnumerable<Frame>> _impl;

        public ScriptedDriver(Func<VoiceTurnContext, CancellationToken, IAsyncEnumerable<Frame>> impl) => _impl = impl;

        public IAsyncEnumerable<Frame> RunTurnAsync(VoiceTurnContext ctx, CancellationToken ct) => _impl(ctx, ct);
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
    /// Builds source → upstreamCapture → background → downstreamCapture → sink. Upstream-travelling
    /// completions surface at <c>Upstream</c>; pass-through frames surface at <c>Downstream</c>.
    /// </summary>
    private static (PipelineRunner Runner, CapturingProcessor Upstream, CapturingProcessor Downstream, Pipeline Pipeline)
        Build(BackgroundAgentProcessor processor)
    {
        var upstream = new CapturingProcessor("UpstreamCapture");
        var downstream = new CapturingProcessor("DownstreamCapture");
        var pipeline = Pipeline.Build()
            .Source(new PipelineSource())
            .Then(upstream)
            .Then(processor)
            .Then(downstream)
            .Sink(new PipelineSink());
        return (new PipelineRunner(pipeline), upstream, downstream, pipeline);
    }

    // ── Round trip ────────────────────────────────────────────────────────

    [Fact]
    public async Task Request_Runs_Driver_And_Pushes_Completion_Upstream()
    {
        var driver = new ScriptedDriver((_, _) => Yield(
            new LlmTextChunkFrame("the answer "),
            new LlmTextChunkFrame("is 42"),
            new LlmUsageFrame(30, 7)));
        var (runner, upstream, _, pipeline) = Build(new BackgroundAgentProcessor(driver));

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new BackgroundTaskRequestFrame("t1", "find the answer", OriginTurnId: "turn-9"));

            await upstream.WaitForAsync(f => f is BackgroundTaskCompletedFrame, TimeSpan.FromSeconds(2));

            var completed = Assert.Single(upstream.Captured.OfType<BackgroundTaskCompletedFrame>());
            Assert.Equal("t1", completed.TaskId);
            Assert.Equal("the answer is 42", completed.ResultText);
            Assert.False(completed.IsError);
            Assert.Equal(30, completed.InputTokens);
            Assert.Equal(7, completed.OutputTokens);
            Assert.Equal("turn-9", completed.OriginTurnId);
            Assert.Equal(FrameDirection.Upstream, completed.Direction);
        }
    }

    [Fact]
    public async Task Driver_Sees_Goal_As_UserText_And_Context_In_Metadata()
    {
        VoiceTurnContext? seen = null;
        var driver = new ScriptedDriver((ctx, _) => { seen = ctx; return Yield(new LlmTextChunkFrame("ok")); });
        var (runner, upstream, _, pipeline) = Build(new BackgroundAgentProcessor(driver));

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new BackgroundTaskRequestFrame("t1", "the goal", ContextJson: "{\"k\":1}", OriginTurnId: "o1"));
            await upstream.WaitForAsync(f => f is BackgroundTaskCompletedFrame, TimeSpan.FromSeconds(2));

            Assert.NotNull(seen);
            Assert.Equal("the goal", seen!.UserText);
            Assert.Equal("t1", seen.TurnId);
            Assert.Equal("{\"k\":1}", seen.Metadata[BackgroundAgentProcessor.ContextJsonMetadataKey]);
            Assert.Equal("o1", seen.Metadata[BackgroundAgentProcessor.OriginTurnIdMetadataKey]);
        }
    }

    // ── Containment (whitelist) ───────────────────────────────────────────

    [Fact]
    public async Task Background_Text_Never_Reaches_Downstream_But_Status_Does()
    {
        var driver = new ScriptedDriver((_, _) => Yield(
            new LlmTextChunkFrame("chunk"),
            new TextFrame("speakable"),                       // TTS synthesizes TextFrame — must be contained
            new StatusFrame("Researching..."),
            new AudioRawFrame(new byte[8], 16000, 1)));       // unknown for a background driver — dropped
        var (runner, upstream, downstream, pipeline) = Build(new BackgroundAgentProcessor(driver));

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new BackgroundTaskRequestFrame("t1", "goal"));
            await upstream.WaitForAsync(f => f is BackgroundTaskCompletedFrame, TimeSpan.FromSeconds(2));

            var completed = Assert.Single(upstream.Captured.OfType<BackgroundTaskCompletedFrame>());
            Assert.Equal("chunkspeakable", completed.ResultText); // TextFrame accumulated, not spoken

            var downstreamFrames = downstream.Captured;
            Assert.Contains(downstreamFrames, f => f is StatusFrame s && s.Message == "Researching...");
            Assert.DoesNotContain(downstreamFrames, f => f is LlmTextChunkFrame);
            Assert.DoesNotContain(downstreamFrames, f => f is TextFrame);
            Assert.DoesNotContain(downstreamFrames, f => f is AudioRawFrame);
            Assert.DoesNotContain(downstreamFrames, f => f is BackgroundTaskRequestFrame); // consumed, not forwarded
        }
    }

    [Fact]
    public async Task Emitter_Applies_The_Same_Whitelist()
    {
        var driver = new ScriptedDriver((ctx, ct) => EmitThenFinish(ctx, ct));
        var (runner, upstream, downstream, pipeline) = Build(new BackgroundAgentProcessor(driver));

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new BackgroundTaskRequestFrame("t1", "goal"));
            await upstream.WaitForAsync(f => f is BackgroundTaskCompletedFrame, TimeSpan.FromSeconds(2));

            Assert.Contains(downstream.Captured, f => f is StatusFrame s && s.Message == "emitted status");
            Assert.DoesNotContain(downstream.Captured, f => f is TextFrame);
        }

        static async IAsyncEnumerable<Frame> EmitThenFinish(VoiceTurnContext ctx, [EnumeratorCancellation] CancellationToken ct)
        {
            await ctx.Emitter.EmitAsync(new StatusFrame("emitted status"), ct);
            await ctx.Emitter.EmitAsync(new TextFrame("emitted speakable"), ct); // dropped by the emitter whitelist
            yield return new LlmTextChunkFrame("done");
        }
    }

    [Fact]
    public async Task Frontend_Tools_Throw_In_Background_Turns()
    {
        var driver = new ScriptedDriver((ctx, ct) => AwaitFrontendTool(ctx, ct));
        var (runner, upstream, _, pipeline) = Build(new BackgroundAgentProcessor(driver));

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new BackgroundTaskRequestFrame("t1", "goal"));
            await upstream.WaitForAsync(f => f is BackgroundTaskCompletedFrame, TimeSpan.FromSeconds(2));

            var completed = Assert.Single(upstream.Captured.OfType<BackgroundTaskCompletedFrame>());
            Assert.True(completed.IsError);
            Assert.Contains("Frontend tools", completed.ResultText);
        }

        static async IAsyncEnumerable<Frame> AwaitFrontendTool(VoiceTurnContext ctx, [EnumeratorCancellation] CancellationToken ct)
        {
            await ctx.FrontendTools.AwaitToolResultAsync("call1", ct);
            yield return new LlmTextChunkFrame("unreachable");
        }
    }

    // ── Rejection, isolation, timeout ─────────────────────────────────────

    [Fact]
    public async Task Request_Beyond_Queue_Cap_Is_Rejected_With_IsError_Completion()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var driver = new ScriptedDriver((ctx, ct) => BlockUntilGate(ctx, ct));
        var processor = new BackgroundAgentProcessor(driver, maxConcurrentTasks: 1, maxQueuedRequests: 1);
        var (runner, upstream, _, pipeline) = Build(processor);

        await using (runner)
        {
            await runner.StartAsync();

            await pipeline.Source.IngestAsync(new BackgroundTaskRequestFrame("running", "goal"));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); // worker dequeued it — queue is empty again

            await pipeline.Source.IngestAsync(new BackgroundTaskRequestFrame("queued", "goal"));   // fills the queue
            await pipeline.Source.IngestAsync(new BackgroundTaskRequestFrame("rejected", "goal")); // over the cap

            await upstream.WaitForAsync(
                f => f is BackgroundTaskCompletedFrame { TaskId: "rejected" }, TimeSpan.FromSeconds(2));
            var rejection = Assert.Single(upstream.Captured.OfType<BackgroundTaskCompletedFrame>());
            Assert.True(rejection.IsError);
            Assert.Contains("queue is full", rejection.ResultText);

            gate.SetResult(); // both accepted tasks complete normally
            await upstream.WaitForAsync(
                f => f is BackgroundTaskCompletedFrame { TaskId: "queued", IsError: false }, TimeSpan.FromSeconds(2));
        }

        async IAsyncEnumerable<Frame> BlockUntilGate(VoiceTurnContext ctx, [EnumeratorCancellation] CancellationToken ct)
        {
            entered.TrySetResult();
            await gate.Task.WaitAsync(ct);
            yield return new LlmTextChunkFrame("done");
        }
    }

    [Fact]
    public async Task Throwing_Driver_Completes_With_IsError_And_Session_Stays_Healthy()
    {
        var calls = 0;
        var driver = new ScriptedDriver((_, _) =>
            Interlocked.Increment(ref calls) == 1 ? Throw() : Yield(new LlmTextChunkFrame("recovered")));
        var (runner, upstream, _, pipeline) = Build(new BackgroundAgentProcessor(driver, maxConcurrentTasks: 1));

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new BackgroundTaskRequestFrame("t1", "goal"));
            await pipeline.Source.IngestAsync(new BackgroundTaskRequestFrame("t2", "goal"));

            await upstream.WaitForAsync(f => f is BackgroundTaskCompletedFrame { TaskId: "t2" }, TimeSpan.FromSeconds(2));

            var results = upstream.Captured.OfType<BackgroundTaskCompletedFrame>().ToList();
            Assert.True(results.Single(r => r.TaskId == "t1").IsError);
            Assert.Contains("boom", results.Single(r => r.TaskId == "t1").ResultText);
            Assert.False(results.Single(r => r.TaskId == "t2").IsError); // per-task isolation
        }

        static async IAsyncEnumerable<Frame> Throw()
        {
            await Task.Yield();
            throw new InvalidOperationException("boom");
#pragma warning disable CS0162 // unreachable — required to make this an iterator
            yield break;
#pragma warning restore CS0162
        }
    }

    [Fact]
    public async Task Timed_Out_Task_Completes_With_IsError_Not_Silence()
    {
        var driver = new ScriptedDriver((ctx, ct) => NeverFinishes(ctx, ct));
        var processor = new BackgroundAgentProcessor(driver, taskTimeout: TimeSpan.FromMilliseconds(100));
        var (runner, upstream, _, pipeline) = Build(processor);

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new BackgroundTaskRequestFrame("t1", "goal"));

            await upstream.WaitForAsync(f => f is BackgroundTaskCompletedFrame, TimeSpan.FromSeconds(3));

            var completed = Assert.Single(upstream.Captured.OfType<BackgroundTaskCompletedFrame>());
            Assert.True(completed.IsError);
            Assert.Contains("timed out", completed.ResultText);
        }

        static async IAsyncEnumerable<Frame> NeverFinishes(VoiceTurnContext ctx, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            yield return new LlmTextChunkFrame("unreachable");
        }
    }

    // ── Interruption & shutdown ───────────────────────────────────────────

    [Fact]
    public async Task Interruption_Does_Not_Cancel_An_InFlight_Task()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var driver = new ScriptedDriver((ctx, ct) => BlockThenAnswer(ctx, ct));
        var (runner, upstream, _, pipeline) = Build(new BackgroundAgentProcessor(driver));

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new BackgroundTaskRequestFrame("t1", "goal"));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            await pipeline.Source.IngestAsync(new InterruptionFrame()); // barge-in mid-task
            gate.SetResult();

            await upstream.WaitForAsync(f => f is BackgroundTaskCompletedFrame, TimeSpan.FromSeconds(2));
            var completed = Assert.Single(upstream.Captured.OfType<BackgroundTaskCompletedFrame>());
            Assert.False(completed.IsError); // the task survived the interruption
            Assert.Equal("survived", completed.ResultText);
        }

        async IAsyncEnumerable<Frame> BlockThenAnswer(VoiceTurnContext ctx, [EnumeratorCancellation] CancellationToken ct)
        {
            entered.TrySetResult();
            await gate.Task.WaitAsync(ct);
            yield return new LlmTextChunkFrame("survived");
        }
    }

    [Fact]
    public async Task EndFrame_Cancels_InFlight_Tasks_And_Still_Reaches_The_Sink()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var driver = new ScriptedDriver((ctx, ct) => BlockForever(ctx, ct));
        var (runner, upstream, downstream, pipeline) = Build(new BackgroundAgentProcessor(driver));

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new BackgroundTaskRequestFrame("t1", "goal"));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            await pipeline.Source.IngestAsync(new EndFrame());
            await runner.WaitAsync().WaitAsync(TimeSpan.FromSeconds(10)); // EndFrame forwarded; teardown didn't hang on the task

            Assert.DoesNotContain(upstream.Captured, f => f is BackgroundTaskCompletedFrame); // no completion after shutdown
        }

        async IAsyncEnumerable<Frame> BlockForever(VoiceTurnContext ctx, [EnumeratorCancellation] CancellationToken ct)
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            yield return new LlmTextChunkFrame("unreachable");
        }
    }

    // ── Transparency ──────────────────────────────────────────────────────

    [Fact]
    public async Task NonRequest_Frames_Pass_Through_Untouched()
    {
        var driver = new ScriptedDriver((_, _) => Yield(new LlmTextChunkFrame("unused")));
        var (runner, _, downstream, pipeline) = Build(new BackgroundAgentProcessor(driver));

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new LlmTextChunkFrame("conversation text"));
            await pipeline.Source.IngestAsync(new TranscriptionFrame("hello", IsFinal: true));

            await downstream.WaitForAsync(f => f is TranscriptionFrame, TimeSpan.FromSeconds(2));
            Assert.Contains(downstream.Captured, f => f is LlmTextChunkFrame c && c.Text == "conversation text");
        }
    }
}
