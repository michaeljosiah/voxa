using Voxa.Frames;
using Voxa.Pipelines;
using Voxa.Processors;
using Voxa.Speech;
using Voxa.Testing.Processors;

namespace Voxa.Speech.Abstractions.Tests;

/// <summary>
/// Covers the eager-first-chunk option (VPS-001 WS8.2): the first flush of a turn may break at a
/// clause boundary; subsequent flushes require a full sentence boundary; a new turn re-enables it.
/// </summary>
public class SentenceAggregatorEagerTests
{
    private static (PipelineRunner Runner, CapturingProcessor Captured, Pipeline Pipeline) Build(SentenceAggregator agg)
    {
        var captured = new CapturingProcessor();
        var pipeline = Pipeline.Build()
            .Source(new PipelineSource())
            .Then(agg)
            .Then(captured)
            .Sink(new PipelineSink());
        return (new PipelineRunner(pipeline), captured, pipeline);
    }

    [Fact]
    public async Task FirstFlush_BreaksAtClauseBoundary_WhenEnoughBuffered()
    {
        var (runner, captured, pipeline) = Build(new SentenceAggregator { EagerFirstChunkMinChars = 20 });
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(40);

            // No sentence boundary yet, but a comma after >= 20 chars on the first flush.
            await pipeline.Source.IngestAsync(new LlmTextChunkFrame("Well, that is a very long opening clause, and more"));
            await Task.Delay(80);

            var first = captured.Captured.OfType<TextFrame>().FirstOrDefault();
            Assert.NotNull(first);
            // Breaks at the LAST clause boundary that satisfies the rule within this chunk.
            Assert.EndsWith(",", first!.Text);
        }
    }

    [Fact]
    public async Task SecondFlush_RequiresSentenceBoundary_NotClause()
    {
        var (runner, captured, pipeline) = Build(new SentenceAggregator { EagerFirstChunkMinChars = 10 });
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(40);

            // First flush eagerly breaks at the clause boundary.
            await pipeline.Source.IngestAsync(new LlmTextChunkFrame("First clause here, "));
            await Task.Delay(60);
            Assert.Single(captured.Captured.OfType<TextFrame>());

            // After the first flush, a clause boundary alone must NOT trigger another flush.
            await pipeline.Source.IngestAsync(new LlmTextChunkFrame("second clause here, still going on and on"));
            await Task.Delay(60);
            Assert.Single(captured.Captured.OfType<TextFrame>());   // still just the one

            // A real sentence boundary flushes.
            await pipeline.Source.IngestAsync(new LlmTextChunkFrame(" done. "));
            await Task.Delay(60);
            Assert.Equal(2, captured.Captured.OfType<TextFrame>().Count());
        }
    }

    [Fact]
    public async Task NewTurn_ReenablesEagerMode()
    {
        var (runner, captured, pipeline) = Build(new SentenceAggregator { EagerFirstChunkMinChars = 10 });
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(40);

            await pipeline.Source.IngestAsync(new LlmTextChunkFrame("Opening clause one, "));
            await Task.Delay(60);
            Assert.Single(captured.Captured.OfType<TextFrame>());

            // New turn boundary re-arms eager mode.
            await pipeline.Source.IngestAsync(new LlmTurnStartedFrame("turn-2"));
            await pipeline.Source.IngestAsync(new LlmTextChunkFrame("Opening clause two, "));
            await Task.Delay(60);
            Assert.Equal(2, captured.Captured.OfType<TextFrame>().Count());
        }
    }

    [Fact]
    public async Task Default_Off_DoesNotBreakAtClause()
    {
        var (runner, captured, pipeline) = Build(new SentenceAggregator());   // EagerFirstChunkMinChars = 0
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(40);

            await pipeline.Source.IngestAsync(new LlmTextChunkFrame("A clause, with no sentence boundary yet and quite long"));
            await Task.Delay(60);

            Assert.DoesNotContain(captured.Captured, f => f is TextFrame);
        }
    }
}
