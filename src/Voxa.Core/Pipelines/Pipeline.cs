using Voxa.Processors;

namespace Voxa.Pipelines;

/// <summary>
/// An ordered chain of <see cref="FrameProcessor"/>s with a <see cref="Processors.PipelineSource"/>
/// at the head and a <see cref="Processors.PipelineSink"/> at the tail. Built via <see cref="Build"/>.
/// </summary>
public sealed class Pipeline
{
    private readonly IReadOnlyList<FrameProcessor> _processors;

    public PipelineSource Source { get; }
    public PipelineSink Sink { get; }
    public IReadOnlyList<FrameProcessor> Processors => _processors;

    internal Pipeline(IReadOnlyList<FrameProcessor> processors, PipelineSource source, PipelineSink sink)
    {
        _processors = processors;
        Source = source;
        Sink = sink;

        for (var i = 0; i < _processors.Count - 1; i++)
        {
            _processors[i].Link(_processors[i + 1]);
        }
    }

    public static PipelineBuilder Build() => new();
}

/// <summary>Fluent builder for <see cref="Pipeline"/>. Insertion order = downstream order.</summary>
public sealed class PipelineBuilder
{
    private PipelineSource? _source;
    private readonly List<FrameProcessor> _middle = new();

    public PipelineBuilder Source(PipelineSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        return this;
    }

    public PipelineBuilder Then(FrameProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        _middle.Add(processor);
        return this;
    }

    public Pipeline Sink(PipelineSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (_source is null)
        {
            throw new InvalidOperationException("Pipeline.Build() requires a Source(...) before Sink(...).");
        }

        var all = new List<FrameProcessor>(2 + _middle.Count) { _source };
        all.AddRange(_middle);
        all.Add(sink);
        return new Pipeline(all, _source, sink);
    }
}
