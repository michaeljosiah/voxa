using System.Threading.Channels;
using Voxa.Frames;
using Voxa.Pipelines;
using Voxa.Processors;

namespace Voxa.Core.Tests;

public class PipelineRunnerTests
{
    private sealed class PassthroughProcessor : FrameProcessor
    {
        protected override ValueTask ProcessFrameAsync(Frame frame, CancellationToken ct)
            => PushFrameAsync(frame, ct);
    }

    private sealed class FailingProcessor : FrameProcessor
    {
        private readonly string _trigger;
        public FailingProcessor(string triggerText) { _trigger = triggerText; }

        protected override async ValueTask ProcessFrameAsync(Frame frame, CancellationToken ct)
        {
            if (frame is TextFrame t && t.Text == _trigger)
            {
                await PushErrorAsync($"Triggered by '{t.Text}'", ct: ct);
                return;
            }
            await PushFrameAsync(frame, ct);
        }
    }

    [Fact]
    public async Task Frame_Flows_Source_To_Sink()
    {
        var pipeline = Pipeline.Build()
            .Source(new PipelineSource())
            .Then(new PassthroughProcessor())
            .Sink(new PipelineSink());

        await using var runner = new PipelineRunner(pipeline);
        await runner.StartAsync();

        await pipeline.Source.IngestAsync(new TextFrame("hello"));

        var collected = new List<Frame>();
        var collectTask = Task.Run(async () =>
        {
            await foreach (var f in pipeline.Sink.ReadAllAsync())
            {
                collected.Add(f);
                if (f is TextFrame) break;
            }
        });

        await collectTask.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Contains(collected, f => f is StartFrame);
        Assert.Contains(collected, f => f is TextFrame t && t.Text == "hello");
    }

    [Fact]
    public async Task StopAsync_Drains_End_Frame_And_Completes_Wait()
    {
        var pipeline = Pipeline.Build()
            .Source(new PipelineSource())
            .Then(new PassthroughProcessor())
            .Sink(new PipelineSink());

        await using var runner = new PipelineRunner(pipeline);
        await runner.StartAsync();

        // Drain sink in background so the EndFrame can flush.
        var sinkDrain = Task.Run(async () =>
        {
            await foreach (var _ in pipeline.Sink.ReadAllAsync()) { }
        });

        await runner.StopAsync(TimeSpan.FromSeconds(2));

        await runner.WaitAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(runner.WaitAsync().IsCompletedSuccessfully);
        await sinkDrain.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ErrorFrame_From_Middle_Processor_Surfaces_As_Exception()
    {
        var pipeline = Pipeline.Build()
            .Source(new PipelineSource())
            .Then(new FailingProcessor("fail"))
            .Sink(new PipelineSink());

        await using var runner = new PipelineRunner(pipeline);
        await runner.StartAsync();

        // Drain sink in background.
        _ = Task.Run(async () =>
        {
            try { await foreach (var _ in pipeline.Sink.ReadAllAsync()) { } } catch { }
        });

        await pipeline.Source.IngestAsync(new TextFrame("fail"));

        var ex = await Assert.ThrowsAsync<PipelineFailedException>(
            async () => await runner.WaitAsync().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Contains("fail", ex.Message);
        Assert.NotNull(ex.ErrorFrame);
    }

    [Fact]
    public async Task Cannot_Start_Twice()
    {
        var pipeline = Pipeline.Build()
            .Source(new PipelineSource())
            .Sink(new PipelineSink());

        await using var runner = new PipelineRunner(pipeline);
        await runner.StartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await runner.StartAsync());
    }

    [Fact]
    public async Task DisposeAsync_Cancels_All_Processors()
    {
        var pipeline = Pipeline.Build()
            .Source(new PipelineSource())
            .Then(new PassthroughProcessor())
            .Sink(new PipelineSink());

        var runner = new PipelineRunner(pipeline);
        await runner.StartAsync();
        await runner.DisposeAsync();

        // After dispose, ingesting should fail (channels completed).
        await Assert.ThrowsAsync<ChannelClosedException>(async () =>
            await pipeline.Source.IngestAsync(new TextFrame("after-dispose")));
    }
}
