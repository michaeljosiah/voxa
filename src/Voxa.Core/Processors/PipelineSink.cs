using System.Threading.Channels;
using Voxa.Frames;

namespace Voxa.Processors;

/// <summary>
/// Tail of a <see cref="Pipelines.Pipeline"/>. Buffers downstream frames into an unbounded channel
/// that external consumers (transports, tests) drain via <see cref="ReadAllAsync"/>. Exposes
/// <see cref="EndFrameObserved"/> so the runner can detect graceful shutdown without competing
/// with the consumer for frames.
/// </summary>
public class PipelineSink : FrameProcessor
{
    private readonly Channel<Frame> _output = Channel.CreateUnbounded<Frame>();
    private readonly TaskCompletionSource<object?> _endFrameObserved =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PipelineSink(string? name = null) : base(name ?? "PipelineSink") { }

    /// <summary>Stream of every downstream frame. Completes when the sink observes an <see cref="EndFrame"/>.</summary>
    public IAsyncEnumerable<Frame> ReadAllAsync(CancellationToken ct = default)
        => _output.Reader.ReadAllAsync(ct);

    /// <summary>Completes when this sink has observed an <see cref="EndFrame"/>. Never throws.</summary>
    public Task EndFrameObserved => _endFrameObserved.Task;

    protected override async ValueTask ProcessFrameAsync(Frame frame, CancellationToken ct)
    {
        if (frame.Direction == FrameDirection.Upstream)
        {
            // Upstream frames bubble back through the sink — forward via the upstream callback.
            await PushFrameAsync(frame, ct).ConfigureAwait(false);
            return;
        }

        await _output.Writer.WriteAsync(frame, ct).ConfigureAwait(false);
        if (frame is EndFrame)
        {
            _endFrameObserved.TrySetResult(null);
            _output.Writer.TryComplete();
        }
    }
}
