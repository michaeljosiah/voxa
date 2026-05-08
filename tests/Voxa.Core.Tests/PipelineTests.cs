using Voxa.Frames;
using Voxa.Pipelines;
using Voxa.Processors;

namespace Voxa.Core.Tests;

public class PipelineTests
{
    private sealed class PassthroughProcessor : FrameProcessor
    {
        protected override ValueTask ProcessFrameAsync(Frame frame, CancellationToken ct)
            => PushFrameAsync(frame, ct);
    }

    [Fact]
    public void Build_Throws_If_Source_Missing()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Pipeline.Build().Sink(new PipelineSink()));
    }

    [Fact]
    public void Build_Wires_Source_Through_Middle_To_Sink()
    {
        var src = new PipelineSource();
        var snk = new PipelineSink();
        var mid1 = new PassthroughProcessor();
        var mid2 = new PassthroughProcessor();

        var pipeline = Pipeline.Build()
            .Source(src)
            .Then(mid1)
            .Then(mid2)
            .Sink(snk);

        Assert.Same(src, pipeline.Source);
        Assert.Same(snk, pipeline.Sink);
        Assert.Equal(4, pipeline.Processors.Count);
        Assert.Same(src, pipeline.Processors[0]);
        Assert.Same(mid1, pipeline.Processors[1]);
        Assert.Same(mid2, pipeline.Processors[2]);
        Assert.Same(snk, pipeline.Processors[3]);
    }

    [Fact]
    public void Build_Without_Middle_Processors_Still_Valid()
    {
        var pipeline = Pipeline.Build()
            .Source(new PipelineSource())
            .Sink(new PipelineSink());
        Assert.Equal(2, pipeline.Processors.Count);
    }
}
