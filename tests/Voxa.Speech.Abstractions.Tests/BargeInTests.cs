using System.Runtime.CompilerServices;
using System.Text;
using Voxa.Frames;
using Voxa.Pipelines;
using Voxa.Processors;
using Voxa.Testing.Processors;

namespace Voxa.Speech.Abstractions.Tests;

/// <summary>
/// VRT-002 WS2 barge-in, end to end: an interrupted answer must STOP — not pause for the queued
/// audio and then resume from the next sentence. Covers the aggregator's stale-turn mute, the TTS
/// abort + mute, and the full agent→aggregator→TTS chain.
/// </summary>
public class BargeInTests
{
    private static readonly TimeSpan WaitCap = TimeSpan.FromSeconds(10);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + WaitCap;
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(5);
    }

    // ── SentenceAggregator: stale chunks after an interruption are dropped ──

    private static (PipelineRunner Runner, CapturingProcessor Captured, Pipeline Pipeline) BuildAggregator()
    {
        var captured = new CapturingProcessor();
        var pipeline = Pipeline.Build()
            .Source(new PipelineSource())
            .Then(new SentenceAggregator())
            .Then(captured)
            .Sink(new PipelineSink());
        return (new PipelineRunner(pipeline), captured, pipeline);
    }

    [Fact]
    public async Task Aggregator_Drops_Chunks_Queued_Behind_The_Interruption_Until_The_Next_Turn()
    {
        var (runner, captured, pipeline) = BuildAggregator();
        await using (runner)
        {
            await runner.StartAsync();

            await pipeline.Source.IngestAsync(new InterruptionFrame());
            await captured.WaitForAsync(f => f is InterruptionFrame, WaitCap);

            // The cancelled turn's chunks arrive AFTER the (system-channel) interruption — a
            // one-shot buffer drop can't catch them; the mute must.
            await pipeline.Source.IngestAsync(new LlmTextChunkFrame("stale sentence one. "));
            await pipeline.Source.IngestAsync(new LlmTextChunkFrame("stale sentence two. "));
            await Task.Delay(80);
            Assert.DoesNotContain(captured.Captured, f => f is TextFrame);

            // A fresh turn reopens the tap.
            await pipeline.Source.IngestAsync(new LlmTurnStartedFrame("t2"));
            await pipeline.Source.IngestAsync(new LlmTextChunkFrame("fresh answer. "));
            await captured.WaitForAsync(f => f is TextFrame t && t.Text.Contains("fresh"), WaitCap);
            Assert.DoesNotContain(captured.Captured, f => f is TextFrame t && t.Text.Contains("stale"));
        }
    }

    [Fact]
    public async Task Aggregator_Unmutes_On_A_Final_Transcription_Too()
    {
        // Chains without turn-lifecycle frames recover at the barge-in utterance's own final.
        var (runner, captured, pipeline) = BuildAggregator();
        await using (runner)
        {
            await runner.StartAsync();

            await pipeline.Source.IngestAsync(new UserStartedSpeakingFrame());
            await captured.WaitForAsync(f => f is UserStartedSpeakingFrame, WaitCap);
            await pipeline.Source.IngestAsync(new LlmTextChunkFrame("stale tail. "));
            await Task.Delay(80);
            Assert.DoesNotContain(captured.Captured, f => f is TextFrame);

            await pipeline.Source.IngestAsync(new TranscriptionFrame("the new question", IsFinal: true));
            await pipeline.Source.IngestAsync(new LlmTextChunkFrame("new answer. "));
            await captured.WaitForAsync(f => f is TextFrame t && t.Text.Contains("new answer"), WaitCap);
        }
    }

    // ── TextToSpeechProcessor: abort in-flight synthesis + mute the stale tail ──

    private sealed class GatedTtsEngine : ITextToSpeechEngine
    {
        public TaskCompletionSource FirstChunkOut { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool SawCancellation;

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(
            string text, [EnumeratorCancellation] CancellationToken ct)
        {
            yield return Encoding.UTF8.GetBytes($"PCM1:{text}");
            FirstChunkOut.TrySetResult();
            try
            {
                await Gate.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                SawCancellation = true;
                throw;
            }
            yield return Encoding.UTF8.GetBytes($"PCM2:{text}");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Tts_Aborts_The_Sentence_Being_Synthesized_When_The_User_Starts_Speaking()
    {
        var engine = new GatedTtsEngine();
        var captured = new CapturingProcessor();
        var pipeline = Pipeline.Build()
            .Source(new PipelineSource())
            .Then(new TextToSpeechProcessor(() => engine))
            .Then(captured)
            .Sink(new PipelineSink());

        await using var runner = new PipelineRunner(pipeline);
        await runner.StartAsync();

        await pipeline.Source.IngestAsync(new TextFrame("a long sentence"));
        await engine.FirstChunkOut.Task.WaitAsync(WaitCap); // mid-synthesis, blocked on the gate

        await pipeline.Source.IngestAsync(new UserStartedSpeakingFrame()); // barge-in over the tail

        await WaitUntilAsync(() => engine.SawCancellation);
        Assert.True(engine.SawCancellation, "in-flight synthesis must abort on barge-in");

        // The bookend still closes, and the second PCM chunk never ships.
        await captured.WaitForAsync(f => f is BotStoppedSpeakingFrame, WaitCap);
        Assert.DoesNotContain(captured.Captured,
            f => f is AudioRawFrame a && Encoding.UTF8.GetString(a.Pcm.Span).StartsWith("PCM2"));
    }

    [Fact]
    public async Task Tts_Mutes_The_Stale_Tail_And_Reopens_On_The_Next_Turn()
    {
        var engine = new ScriptedTextToSpeechEngine();
        var captured = new CapturingProcessor();
        var pipeline = Pipeline.Build()
            .Source(new PipelineSource())
            .Then(new TextToSpeechProcessor(() => engine))
            .Then(captured)
            .Sink(new PipelineSink());

        await using var runner = new PipelineRunner(pipeline);
        await runner.StartAsync();

        await pipeline.Source.IngestAsync(new InterruptionFrame());
        await captured.WaitForAsync(f => f is InterruptionFrame, WaitCap);

        await pipeline.Source.IngestAsync(new TextFrame("stale queued sentence"));
        await Task.Delay(80);
        Assert.Empty(engine.SynthesizeCalls); // neither spoken…
        Assert.DoesNotContain(captured.Captured, f => f is TextFrame); // …nor rendered

        await pipeline.Source.IngestAsync(new LlmTurnStartedFrame("t2"));
        await pipeline.Source.IngestAsync(new TextFrame("fresh sentence"));
        await captured.WaitForAsync(f => f is AudioRawFrame, WaitCap);
        Assert.Equal(["fresh sentence"], engine.SynthesizeCalls);
    }

    // ── End to end: agent → aggregator → TTS. The repro for "the answer resumes". ──

    private sealed class GatedStreamingDriver : IAgentTurnDriver
    {
        public TaskCompletionSource FirstSentenceOut { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool SawCancellation;

        public async IAsyncEnumerable<Frame> RunTurnAsync(
            VoiceTurnContext ctx, [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new LlmTextChunkFrame("First sentence of the answer. ");
            FirstSentenceOut.TrySetResult();
            bool cancelled = false;
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct); // "still generating…"
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                SawCancellation = true;
            }
            if (cancelled) yield break;
            yield return new LlmTextChunkFrame("Resumed sentence that must never be heard. ");
        }
    }

    [Fact]
    public async Task An_Interrupted_Answer_Stops_And_Does_Not_Resume()
    {
        var driver = new GatedStreamingDriver();
        var engine = new ScriptedTextToSpeechEngine();
        var captured = new CapturingProcessor();
        var pipeline = Pipeline.Build()
            .Source(new PipelineSource())
            .Then(new AgentLoopProcessor(driver))
            .Then(new SentenceAggregator())
            .Then(new TextToSpeechProcessor(() => engine))
            .Then(captured)
            .Sink(new PipelineSink());

        await using var runner = new PipelineRunner(pipeline);
        await runner.StartAsync();

        // The user asks; the bot starts answering.
        await pipeline.Source.IngestAsync(new TranscriptionFrame("tell me a long story", IsFinal: true));
        await driver.FirstSentenceOut.Task.WaitAsync(WaitCap);
        await captured.WaitForAsync(f => f is AudioRawFrame, WaitCap); // first sentence is being spoken

        // Barge-in mid-answer.
        await pipeline.Source.IngestAsync(new UserStartedSpeakingFrame());

        await WaitUntilAsync(() => driver.SawCancellation);
        Assert.True(driver.SawCancellation, "the LLM stream must be cancelled by barge-in");
        await captured.WaitForAsync(f => f is InterruptionFrame, WaitCap); // the sink/client would purge + flush on this

        // The decisive assertion — before this fix, the resumed sentence re-aggregated, re-synthesized,
        // and played in the sink's fresh epoch: the bot audibly resumed the interrupted answer.
        await Task.Delay(150);
        Assert.DoesNotContain(engine.SynthesizeCalls, s => s.Contains("never be heard"));
        Assert.DoesNotContain(captured.Captured, f => f is TextFrame t && t.Text.Contains("never be heard"));

        // And the pipeline recovers: the barge-in utterance's own turn runs and is spoken.
        await pipeline.Source.IngestAsync(new TranscriptionFrame("actually, stop", IsFinal: true));
        await WaitUntilAsync(() => engine.SynthesizeCalls.Any(s => s.Contains("First sentence")) &&
                                   engine.SynthesizeCalls.Count >= 2);
        Assert.Contains(engine.SynthesizeCalls, s => s.Contains("First sentence")); // both turns' openings spoken
    }
}
