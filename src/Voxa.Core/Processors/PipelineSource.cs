using System.Threading.Channels;
using Voxa.Frames;

namespace Voxa.Processors;

/// <summary>
/// Entry point of a <see cref="Pipelines.Pipeline"/>. External code (transports, tests) injects
/// frames via <see cref="IngestAsync"/>; the source's drain loop forwards them downstream.
///
/// Upstream frames (errors bubbled back from inner processors) emerge here and are exposed via
/// <see cref="ReadUpstreamAsync"/> — that's how the runner observes pipeline failures.
/// </summary>
public class PipelineSource : FrameProcessor
{
    private readonly Channel<Frame> _upstreamOutput = Channel.CreateUnbounded<Frame>();

    public PipelineSource(string? name = null) : base(name ?? "PipelineSource") { }

    /// <summary>Inject a frame at the head of the pipeline.</summary>
    public ValueTask IngestAsync(Frame frame, CancellationToken ct = default)
        => QueueFrameAsync(frame, ct);

    /// <summary>Stream of frames that have bubbled upstream to the source. Completes on error or end.</summary>
    public IAsyncEnumerable<Frame> ReadUpstreamAsync(CancellationToken ct = default)
        => _upstreamOutput.Reader.ReadAllAsync(ct);

    protected override async ValueTask ProcessFrameAsync(Frame frame, CancellationToken ct)
    {
        if (frame.Direction == FrameDirection.Upstream)
        {
            await _upstreamOutput.Writer.WriteAsync(frame, ct).ConfigureAwait(false);
            if (frame is ErrorFrame)
            {
                _upstreamOutput.Writer.TryComplete();
            }
        }
        else
        {
            await PushFrameAsync(frame, ct).ConfigureAwait(false);
        }
    }
}
