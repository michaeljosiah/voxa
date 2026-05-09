using Voxa.Frames;
using Voxa.Pipelines;
using Voxa.Processors;
using Voxa.Speech;
using Voxa.Testing.Processors;

namespace Voxa.Speech.Abstractions.Tests;

public class SentenceAggregatorTests
{
    private static (PipelineRunner Runner, CapturingProcessor Captured, Pipeline Pipeline) Build(
        SentenceAggregator? aggregator = null)
    {
        aggregator ??= new SentenceAggregator();
        var captured = new CapturingProcessor();
        var pipeline = Pipeline.Build()
            .Source(new PipelineSource())
            .Then(aggregator)
            .Then(captured)
            .Sink(new PipelineSink());
        return (new PipelineRunner(pipeline), captured, pipeline);
    }

    [Fact]
    public async Task Buffers_Tokens_Until_Sentence_Boundary()
    {
        var (runner, captured, pipeline) = Build();
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(40);

            await pipeline.Source.IngestAsync(new LlmTextChunkFrame("Hel"));
            await pipeline.Source.IngestAsync(new LlmTextChunkFrame("lo"));
            await Task.Delay(40);

            // No sentence boundary yet — nothing emitted.
            Assert.DoesNotContain(captured.Captured, f => f is TextFrame);

            await pipeline.Source.IngestAsync(new LlmTextChunkFrame(" world."));
            await Task.Delay(80);

            var emitted = captured.Captured.OfType<TextFrame>().FirstOrDefault();
            Assert.NotNull(emitted);
            Assert.Equal("Hello world.", emitted!.Text);
        }
    }

    [Fact]
    public async Task Emits_Multiple_Sentences_As_Separate_Frames()
    {
        var (runner, captured, pipeline) = Build();
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(40);

            await pipeline.Source.IngestAsync(new LlmTextChunkFrame("First sentence. Second sentence! Third?"));
            await Task.Delay(80);

            var emitted = captured.Captured.OfType<TextFrame>().Select(t => t.Text).ToList();
            Assert.Single(emitted);
            // All three sentences flushed in one combined frame because they arrived in one chunk.
            Assert.Equal("First sentence. Second sentence! Third?", emitted[0]);
        }
    }

    [Fact]
    public async Task Emits_First_Sentence_Eagerly_While_Next_Still_Buffering()
    {
        // The whole point: TTS gets the first sentence ASAP, doesn't wait for the LLM to finish.
        // The aggregator is "eager" — sentence terminators at the end of the current buffer flush
        // immediately, so TTS can start synthesising while the next tokens are still streaming in.
        var (runner, captured, pipeline) = Build();
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(40);

            await pipeline.Source.IngestAsync(new LlmTextChunkFrame("Sure, I can help. "));
            await Task.Delay(40);

            var first = captured.Captured.OfType<TextFrame>().FirstOrDefault();
            Assert.NotNull(first);
            Assert.Equal("Sure, I can help.", first!.Text);

            // Mid-sentence chunks — no boundary yet, nothing flushes.
            await pipeline.Source.IngestAsync(new LlmTextChunkFrame("What do you "));
            await Task.Delay(20);
            Assert.Single(captured.Captured.OfType<TextFrame>());

            // Final chunk completes the second sentence with a "?" — flushes immediately.
            await pipeline.Source.IngestAsync(new LlmTextChunkFrame("need?"));
            await Task.Delay(40);

            var emitted = captured.Captured.OfType<TextFrame>().Select(t => t.Text).ToList();
            Assert.Equal(2, emitted.Count);
            Assert.Equal("What do you need?", emitted[1]);
        }
    }

    [Fact]
    public async Task Does_Not_Split_On_Decimal_Point()
    {
        // "3.14" should NOT be split — the period isn't followed by whitespace.
        var (runner, captured, pipeline) = Build();
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(40);

            await pipeline.Source.IngestAsync(new LlmTextChunkFrame("Pi is 3.14 approximately."));
            await Task.Delay(40);

            var emitted = captured.Captured.OfType<TextFrame>().FirstOrDefault();
            Assert.NotNull(emitted);
            Assert.Equal("Pi is 3.14 approximately.", emitted!.Text);
        }
    }

    [Fact]
    public async Task Force_Flushes_When_Buffer_Exceeds_Max()
    {
        var (runner, captured, pipeline) = Build(new SentenceAggregator { MaxBufferChars = 30 });
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(40);

            // No sentence boundary, but exceeds 30 chars — should force-flush.
            await pipeline.Source.IngestAsync(new LlmTextChunkFrame("This is a very long token stream that has no period in it yet"));
            await Task.Delay(40);

            var emitted = captured.Captured.OfType<TextFrame>().FirstOrDefault();
            Assert.NotNull(emitted);
            Assert.Contains("very long", emitted!.Text);
        }
    }

    [Fact]
    public async Task User_Started_Speaking_Drops_Buffer()
    {
        // If user interrupts mid-response, the half-formed sentence should be dropped, not spoken.
        var (runner, captured, pipeline) = Build();
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(40);

            await pipeline.Source.IngestAsync(new LlmTextChunkFrame("As I was saying, the quarterly"));
            await pipeline.Source.IngestAsync(new UserStartedSpeakingFrame());
            await Task.Delay(40);

            // The interruption frame itself flows through, but no TextFrame from the partial buffer.
            Assert.Contains(captured.Captured, f => f is UserStartedSpeakingFrame);
            Assert.DoesNotContain(captured.Captured, f => f is TextFrame);
        }
    }

    [Fact]
    public async Task End_Frame_Flushes_Trailing_Buffer()
    {
        var (runner, captured, pipeline) = Build();
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(40);

            await pipeline.Source.IngestAsync(new LlmTextChunkFrame("Trailing fragment without period"));
            await Task.Delay(40);

            // No emission yet.
            Assert.DoesNotContain(captured.Captured, f => f is TextFrame);

            await runner.StopAsync();
            // OnEndAsync should flush the leftover.
            var emitted = captured.Captured.OfType<TextFrame>().FirstOrDefault();
            Assert.NotNull(emitted);
            Assert.Equal("Trailing fragment without period", emitted!.Text);
        }
    }
}
