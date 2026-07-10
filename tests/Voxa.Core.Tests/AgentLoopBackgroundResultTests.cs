using Voxa.Frames;
using Voxa.Pipelines;
using Voxa.Processors;
using Voxa.Testing.Processors;

namespace Voxa.Core.Tests;

/// <summary>
/// VDX-008 §4/§4.1: background-result turns and their arbitration against live conversation.
/// The full delegation round trip (loop + BackgroundAgentProcessor) is at the bottom.
/// </summary>
public class AgentLoopBackgroundResultTests
{
    private sealed class RecordingDriver : IAgentTurnDriver
    {
        private readonly List<VoiceTurnContext> _turns = new();

        /// <summary>Snapshot of every context the driver has been invoked with. Thread-safe.</summary>
        public IReadOnlyList<VoiceTurnContext> Turns
        {
            get { lock (_turns) return _turns.ToList(); }
        }

        public async IAsyncEnumerable<Frame> RunTurnAsync(VoiceTurnContext ctx, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            lock (_turns) _turns.Add(ctx);
            yield return new LlmTextChunkFrame(ctx.Trigger == TurnTrigger.BackgroundResult
                ? $"result:{ctx.BackgroundResult!.TaskId}"
                : $"user:{ctx.UserText}");
            await Task.Yield();
        }
    }

    private static (PipelineRunner Runner, CapturingProcessor Captured, Pipeline Pipeline, RecordingDriver Driver)
        Build(BackgroundResultOptions? options = null)
    {
        var driver = new RecordingDriver();
        var processor = new AgentLoopProcessor(driver, backgroundResults: options);
        var captured = new CapturingProcessor();
        var pipeline = Pipeline.Build()
            .Source(new PipelineSource())
            .Then(processor)
            .Then(captured)
            .Sink(new PipelineSink());
        return (new PipelineRunner(pipeline), captured, pipeline, driver);
    }

    private static BackgroundTaskCompletedFrame Result(string taskId) => new(taskId, $"answer-{taskId}");

    // ── Background-result turns are ordinary turns ────────────────────────

    [Fact]
    public async Task Completed_Frame_Runs_A_Turn_With_BackgroundResult_Trigger_And_Lifecycle_Frames()
    {
        var (runner, captured, pipeline, driver) = Build();

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(Result("t1"));

            await captured.WaitForAsync(f => f is LlmTurnEndedFrame, TimeSpan.FromSeconds(2));

            var turn = Assert.Single(driver.Turns);
            Assert.Equal(TurnTrigger.BackgroundResult, turn.Trigger);
            Assert.Equal("t1", turn.BackgroundResult!.TaskId);
            Assert.Equal(string.Empty, turn.UserText);

            var started = Assert.Single(captured.Captured.OfType<LlmTurnStartedFrame>());
            var ended = Assert.Single(captured.Captured.OfType<LlmTurnEndedFrame>());
            Assert.Equal(TurnTrigger.BackgroundResult, started.Trigger);
            Assert.Equal(TurnTrigger.BackgroundResult, ended.Trigger);
            Assert.Equal(started.TurnId, ended.TurnId);

            // Consumed by the loop — never forwarded downstream.
            Assert.DoesNotContain(captured.Captured, f => f is BackgroundTaskCompletedFrame);
        }
    }

    [Fact]
    public async Task User_Turns_Still_Tag_UserUtterance()
    {
        var (runner, captured, pipeline, driver) = Build();

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new TranscriptionFrame("hello", IsFinal: true));
            await captured.WaitForAsync(f => f is LlmTurnEndedFrame, TimeSpan.FromSeconds(2));

            Assert.Equal(TurnTrigger.UserUtterance, Assert.Single(driver.Turns).Trigger);
            Assert.Equal(TurnTrigger.UserUtterance, Assert.Single(captured.Captured.OfType<LlmTurnStartedFrame>()).Trigger);
        }
    }

    // ── Arbitration: hold + data-ordered release (VDX-008 §4.1) ───────────

    [Fact]
    public async Task Result_During_Speech_Is_Held_And_StopSpeaking_Alone_Does_Not_Release()
    {
        // Long timeout so the quiet-timeout fallback can't fire inside this test's window.
        var (runner, captured, pipeline, driver) = Build(new BackgroundResultOptions
        {
            HeldResultReleaseTimeout = TimeSpan.FromSeconds(30),
        });

        await using (runner)
        {
            await runner.StartAsync();

            await pipeline.Source.IngestAsync(new UserStartedSpeakingFrame());
            await captured.WaitForAsync(f => f is UserStartedSpeakingFrame, TimeSpan.FromSeconds(2));

            await pipeline.Source.IngestAsync(Result("t1"));
            await Task.Delay(100);
            Assert.Empty(driver.Turns); // held, not run

            await pipeline.Source.IngestAsync(new UserStoppedSpeakingFrame());
            await captured.WaitForAsync(f => f is UserStoppedSpeakingFrame, TimeSpan.FromSeconds(2));
            await Task.Delay(100);
            Assert.Empty(driver.Turns); // stop-speaking must NOT release — the final is the signal
        }
    }

    [Fact]
    public async Task Final_Transcription_Releases_Held_Results_Behind_The_User_Turn()
    {
        var (runner, captured, pipeline, driver) = Build(new BackgroundResultOptions
        {
            HeldResultReleaseTimeout = TimeSpan.FromSeconds(30),
        });

        await using (runner)
        {
            await runner.StartAsync();

            await pipeline.Source.IngestAsync(new UserStartedSpeakingFrame());
            await captured.WaitForAsync(f => f is UserStartedSpeakingFrame, TimeSpan.FromSeconds(2));
            await pipeline.Source.IngestAsync(Result("t1"));
            await pipeline.Source.IngestAsync(new UserStoppedSpeakingFrame());

            await pipeline.Source.IngestAsync(new TranscriptionFrame("what did you find", IsFinal: true));

            await captured.WaitForAsync(
                f => f is LlmTextChunkFrame c && c.Text == "result:t1", TimeSpan.FromSeconds(2));

            // Data-ordered: the user's turn ran FIRST, the released result turn second.
            var turns = driver.Turns;
            Assert.Equal(2, turns.Count);
            Assert.Equal(TurnTrigger.UserUtterance, turns[0].Trigger);
            Assert.Equal("what did you find", turns[0].UserText);
            Assert.Equal(TurnTrigger.BackgroundResult, turns[1].Trigger);
        }
    }

    [Fact]
    public async Task WhitespaceOnly_Final_Releases_Held_Results_Too()
    {
        var (runner, captured, pipeline, driver) = Build(new BackgroundResultOptions
        {
            HeldResultReleaseTimeout = TimeSpan.FromSeconds(30),
        });

        await using (runner)
        {
            await runner.StartAsync();

            await pipeline.Source.IngestAsync(new UserStartedSpeakingFrame());
            await captured.WaitForAsync(f => f is UserStartedSpeakingFrame, TimeSpan.FromSeconds(2));
            await pipeline.Source.IngestAsync(Result("t1"));

            // Silence misfire: an empty final means no turn will follow this utterance.
            await pipeline.Source.IngestAsync(new TranscriptionFrame("   ", IsFinal: true));

            await captured.WaitForAsync(
                f => f is LlmTextChunkFrame c && c.Text == "result:t1", TimeSpan.FromSeconds(2));
            var turn = Assert.Single(driver.Turns);
            Assert.Equal(TurnTrigger.BackgroundResult, turn.Trigger);
        }
    }

    [Fact]
    public async Task Quiet_Timeout_Releases_When_No_Final_Ever_Arrives()
    {
        var (runner, captured, pipeline, driver) = Build(new BackgroundResultOptions
        {
            HeldResultReleaseTimeout = TimeSpan.FromMilliseconds(150),
        });

        await using (runner)
        {
            await runner.StartAsync();

            await pipeline.Source.IngestAsync(new UserStartedSpeakingFrame());
            await captured.WaitForAsync(f => f is UserStartedSpeakingFrame, TimeSpan.FromSeconds(2));
            await pipeline.Source.IngestAsync(Result("t1"));
            await pipeline.Source.IngestAsync(new UserStoppedSpeakingFrame()); // arms the fallback

            await captured.WaitForAsync(
                f => f is LlmTextChunkFrame c && c.Text == "result:t1", TimeSpan.FromSeconds(3));
            Assert.Equal(TurnTrigger.BackgroundResult, Assert.Single(driver.Turns).Trigger);
        }
    }

    [Fact]
    public async Task A_New_Utterance_Invalidates_A_Pending_Quiet_Timeout()
    {
        var (runner, captured, pipeline, driver) = Build(new BackgroundResultOptions
        {
            HeldResultReleaseTimeout = TimeSpan.FromMilliseconds(150),
        });

        await using (runner)
        {
            await runner.StartAsync();

            await pipeline.Source.IngestAsync(new UserStartedSpeakingFrame());
            await captured.WaitForAsync(f => f is UserStartedSpeakingFrame, TimeSpan.FromSeconds(2));
            await pipeline.Source.IngestAsync(Result("t1"));
            await pipeline.Source.IngestAsync(new UserStoppedSpeakingFrame());

            // The user resumes before the timeout fires — the stale timer must not release mid-utterance.
            await pipeline.Source.IngestAsync(new UserStartedSpeakingFrame());
            await captured.WaitForAsync(f => captured.Captured.OfType<UserStartedSpeakingFrame>().Count() == 2, TimeSpan.FromSeconds(2));
            await Task.Delay(300); // well past the armed timeout
            Assert.Empty(driver.Turns);

            // Their utterance's final still releases it, data-ordered.
            await pipeline.Source.IngestAsync(new TranscriptionFrame("resumed thought", IsFinal: true));
            await captured.WaitForAsync(
                f => f is LlmTextChunkFrame c && c.Text == "result:t1", TimeSpan.FromSeconds(2));
            Assert.Equal(2, driver.Turns.Count);
            Assert.Equal(TurnTrigger.UserUtterance, driver.Turns[0].Trigger);
        }
    }

    [Fact]
    public async Task Hold_Disabled_Enqueues_Immediately_Even_While_User_Speaks()
    {
        var (runner, captured, pipeline, driver) = Build(new BackgroundResultOptions
        {
            HoldWhileUserSpeaking = false,
        });

        await using (runner)
        {
            await runner.StartAsync();

            await pipeline.Source.IngestAsync(new UserStartedSpeakingFrame());
            await captured.WaitForAsync(f => f is UserStartedSpeakingFrame, TimeSpan.FromSeconds(2));
            await pipeline.Source.IngestAsync(Result("t1"));

            await captured.WaitForAsync(
                f => f is LlmTextChunkFrame c && c.Text == "result:t1", TimeSpan.FromSeconds(2));
            Assert.Equal(TurnTrigger.BackgroundResult, Assert.Single(driver.Turns).Trigger);
        }
    }

    [Fact]
    public async Task Held_Results_Beyond_The_Cap_Drop_Oldest()
    {
        var (runner, captured, pipeline, driver) = Build(new BackgroundResultOptions
        {
            MaxPendingResults = 2,
            HeldResultReleaseTimeout = TimeSpan.FromSeconds(30),
        });

        await using (runner)
        {
            await runner.StartAsync();

            await pipeline.Source.IngestAsync(new UserStartedSpeakingFrame());
            await captured.WaitForAsync(f => f is UserStartedSpeakingFrame, TimeSpan.FromSeconds(2));

            await pipeline.Source.IngestAsync(Result("t1"));
            await pipeline.Source.IngestAsync(Result("t2"));
            await pipeline.Source.IngestAsync(Result("t3")); // evicts t1 (drop-oldest)

            await pipeline.Source.IngestAsync(new TranscriptionFrame("done talking", IsFinal: true));

            await captured.WaitForAsync(
                f => f is LlmTextChunkFrame c && c.Text == "result:t3", TimeSpan.FromSeconds(2));

            var backgroundTurns = driver.Turns.Where(t => t.Trigger == TurnTrigger.BackgroundResult).ToList();
            Assert.Equal(2, backgroundTurns.Count);
            Assert.Equal("t2", backgroundTurns[0].BackgroundResult!.TaskId);
            Assert.Equal("t3", backgroundTurns[1].BackgroundResult!.TaskId);
        }
    }

    // ── Full delegation round trip (loop + BackgroundAgentProcessor) ──────

    [Fact]
    public async Task Delegation_Round_Trip_Speaks_The_Result_As_A_Second_Turn()
    {
        // Interaction driver: delegates on the user turn, speaks the result on the background turn.
        var interaction = new DelegatingDriver();
        var background = new AnsweringDriver();

        var loop = new AgentLoopProcessor(interaction);
        var backgroundProcessor = new BackgroundAgentProcessor(background);
        var captured = new CapturingProcessor();
        var pipeline = Pipeline.Build()
            .Source(new PipelineSource())
            .Then(loop)
            .Then(backgroundProcessor)
            .Then(captured)
            .Sink(new PipelineSink());

        await using var runner = new PipelineRunner(pipeline);
        await runner.StartAsync();
        await pipeline.Source.IngestAsync(new TranscriptionFrame("look this up", IsFinal: true));

        await captured.WaitForAsync(
            f => f is LlmTextChunkFrame c && c.Text.StartsWith("result:"), TimeSpan.FromSeconds(3));

        var starts = captured.Captured.OfType<LlmTurnStartedFrame>().ToList();
        Assert.Equal(2, starts.Count);
        Assert.Equal(TurnTrigger.UserUtterance, starts[0].Trigger);
        Assert.Equal(TurnTrigger.BackgroundResult, starts[1].Trigger);

        var chunks = captured.Captured.OfType<LlmTextChunkFrame>().Select(c => c.Text).ToList();
        Assert.Contains("on it", chunks);
        Assert.Contains("result:42 (task task-1)", chunks);
        Assert.DoesNotContain(captured.Captured, f => f is BackgroundTaskRequestFrame);   // consumed by the background stage
        Assert.DoesNotContain(captured.Captured, f => f is BackgroundTaskCompletedFrame); // consumed by the loop
    }

    private sealed class DelegatingDriver : IAgentTurnDriver
    {
        public async IAsyncEnumerable<Frame> RunTurnAsync(VoiceTurnContext ctx, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            if (ctx.Trigger == TurnTrigger.BackgroundResult)
            {
                var r = ctx.BackgroundResult!;
                yield return new LlmTextChunkFrame($"result:{r.ResultText} (task {r.TaskId})");
            }
            else
            {
                yield return new BackgroundTaskRequestFrame("task-1", ctx.UserText, OriginTurnId: ctx.TurnId);
                yield return new LlmTextChunkFrame("on it");
            }
            await Task.Yield();
        }
    }

    private sealed class AnsweringDriver : IAgentTurnDriver
    {
        public async IAsyncEnumerable<Frame> RunTurnAsync(VoiceTurnContext ctx, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            yield return new LlmTextChunkFrame("42");
            await Task.Yield();
        }
    }
}
