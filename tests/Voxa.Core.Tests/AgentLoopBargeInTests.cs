using System.Runtime.CompilerServices;
using Voxa.Frames;
using Voxa.Pipelines;
using Voxa.Processors;
using Voxa.Testing.Processors;

namespace Voxa.Core.Tests;

/// <summary>
/// VRT-002 WS2 barge-in (the deferred half, now shipped): user speech during an in-flight turn
/// cancels the driver enumeration and pushes a real <see cref="InterruptionFrame"/> downstream —
/// previously nothing in a granular pipeline emitted one, so the interrupted answer resumed.
/// </summary>
public class AgentLoopBargeInTests
{
    /// <summary>Driver that yields one chunk, then blocks on a gate observing the turn token.</summary>
    private sealed class GatedDriver : IAgentTurnDriver
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool SawCancellation;
        public volatile int Invocations;

        public async IAsyncEnumerable<Frame> RunTurnAsync(
            VoiceTurnContext ctx, [EnumeratorCancellation] CancellationToken ct)
        {
            Invocations++; // single turn worker — no torn increments
            yield return new LlmTextChunkFrame("First sentence. ");
            Entered.TrySetResult();
            bool cancelled = false;
            try
            {
                await Gate.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                SawCancellation = true;
            }
            if (cancelled) throw new OperationCanceledException(ct);
            yield return new LlmTextChunkFrame("Second sentence that must never be spoken.");
        }
    }

    private static (PipelineRunner Runner, CapturingProcessor Captured, Pipeline Pipeline) Build(
        IAgentTurnDriver driver,
        Func<VoiceTurnContext, TurnSummary, CancellationToken, ValueTask>? onTurnCompleted = null,
        bool cancelTurnOnBargeIn = true)
    {
        var processor = new AgentLoopProcessor(
            driver, onTurnCompleted: onTurnCompleted, cancelTurnOnBargeIn: cancelTurnOnBargeIn);
        var captured = new CapturingProcessor();
        var pipeline = Pipeline.Build()
            .Source(new PipelineSource())
            .Then(processor)
            .Then(captured)
            .Sink(new PipelineSink());
        return (new PipelineRunner(pipeline), captured, pipeline);
    }

    [Fact]
    public async Task User_Speech_During_A_Turn_Cancels_The_Driver_And_Emits_An_Interruption()
    {
        var driver = new GatedDriver();
        TurnSummary? summary = null;
        var (runner, captured, pipeline) = Build(driver, (_, s, _) => { summary = s; return ValueTask.CompletedTask; });

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new TranscriptionFrame("tell me everything", IsFinal: true));
            await driver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); // mid-turn, blocked on the gate

            await pipeline.Source.IngestAsync(new UserStartedSpeakingFrame()); // barge-in

            await captured.WaitForAsync(f => f is LlmTurnEndedFrame, TimeSpan.FromSeconds(5));

            Assert.True(driver.SawCancellation, "the driver's token must fire on barge-in");
            Assert.Contains(captured.Captured, f => f is InterruptionFrame);  // downstream told
            Assert.NotNull(summary);                                          // truncated completion, not an error
            Assert.Equal("First sentence. ", summary!.AssistantText);         // partial answer kept for memory
            Assert.DoesNotContain(captured.Captured,
                f => f is LlmTextChunkFrame c && c.Text.Contains("never be spoken"));
            Assert.DoesNotContain(captured.Captured, f => f is ErrorFrame);   // barge-in is not a failure
        }
    }

    [Fact]
    public async Task A_Cancelled_Turn_Recovers_The_Next_Final_Runs_Normally()
    {
        var driver = new GatedDriver();
        var (runner, captured, pipeline) = Build(driver);

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new TranscriptionFrame("first question", IsFinal: true));
            await driver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await pipeline.Source.IngestAsync(new UserStartedSpeakingFrame());
            await captured.WaitForAsync(f => f is LlmTurnEndedFrame, TimeSpan.FromSeconds(5));

            await pipeline.Source.IngestAsync(new TranscriptionFrame("second question", IsFinal: true));
            await captured.WaitForAsync(
                _ => captured.Captured.OfType<LlmTurnStartedFrame>().Count() == 2, TimeSpan.FromSeconds(5));

            Assert.Equal(2, driver.Invocations); // the worker survived the cancelled turn
        }
    }

    [Fact]
    public async Task User_Speech_With_No_Active_Turn_Emits_No_Interruption()
    {
        var driver = new GatedDriver();
        var (runner, captured, pipeline) = Build(driver);

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new UserStartedSpeakingFrame()); // idle — a normal utterance start
            await captured.WaitForAsync(f => f is UserStartedSpeakingFrame, TimeSpan.FromSeconds(5));

            Assert.DoesNotContain(captured.Captured, f => f is InterruptionFrame);
        }
    }

    [Fact]
    public async Task An_External_InterruptionFrame_Also_Cancels_The_Turn()
    {
        // Composite processors and hand-built hosts emit InterruptionFrame themselves — the loop
        // must honor it, and forward the original without minting a second one.
        var driver = new GatedDriver();
        var (runner, captured, pipeline) = Build(driver);

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new TranscriptionFrame("question", IsFinal: true));
            await driver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await pipeline.Source.IngestAsync(new InterruptionFrame());
            await captured.WaitForAsync(f => f is LlmTurnEndedFrame, TimeSpan.FromSeconds(5));

            Assert.True(driver.SawCancellation);
            Assert.Single(captured.Captured.OfType<InterruptionFrame>()); // forwarded, not duplicated
        }
    }

    [Fact]
    public async Task Opt_Out_Keeps_The_Old_Behavior()
    {
        var driver = new GatedDriver();
        var (runner, captured, pipeline) = Build(driver, cancelTurnOnBargeIn: false);

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new TranscriptionFrame("question", IsFinal: true));
            await driver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await pipeline.Source.IngestAsync(new UserStartedSpeakingFrame());
            await captured.WaitForAsync(f => f is UserStartedSpeakingFrame, TimeSpan.FromSeconds(5));
            await Task.Delay(100);

            Assert.False(driver.SawCancellation);
            Assert.DoesNotContain(captured.Captured, f => f is InterruptionFrame);

            driver.Gate.SetResult(); // let the turn finish normally
            await captured.WaitForAsync(f => f is LlmTurnEndedFrame, TimeSpan.FromSeconds(5));
            Assert.Contains(captured.Captured,
                f => f is LlmTextChunkFrame c && c.Text.Contains("never be spoken")); // old (broken) behavior, by request
        }
    }
}
