using System.Threading.Channels;
using Voxa.Frames;

namespace Voxa.Processors;

/// <summary>
/// Base class for all pipeline nodes. Subclasses override <see cref="ProcessFrameAsync"/> to
/// transform frames; the rest is plumbing.
///
/// Two concurrent tasks run per processor:
/// <list type="bullet">
/// <item>System task — drains an unbounded priority channel for <see cref="SystemFrame"/>s.
/// On <see cref="InterruptionFrame"/>, cancels the in-flight data frame's token.</item>
/// <item>Data task — drains a bounded channel for <see cref="DataFrame"/>s and
/// <see cref="ControlFrame"/>s in order. Each frame gets its own linked CTS.</item>
/// </list>
///
/// Linking with <see cref="Link"/> wires both directions: this processor's downstream → next's input,
/// and next's upstream → this processor's input. Errors travel upstream, data downstream.
/// </summary>
public abstract class FrameProcessor : IAsyncDisposable
{
    private readonly Channel<Frame> _systemChannel = Channel.CreateUnbounded<Frame>();
    private readonly Channel<Frame> _dataChannel;
    private readonly CancellationTokenSource _processorCts = new();

    private Func<Frame, CancellationToken, ValueTask>? _downstream;
    private Func<Frame, CancellationToken, ValueTask>? _upstream;
    private CancellationTokenSource? _currentFrameCts;
    private Task? _systemTask;
    private Task? _dataTask;
    private int _started;

    /// <summary>Human-friendly name for logs and traces. Defaults to the type name.</summary>
    public string Name { get; }

    protected FrameProcessor(string? name = null, BoundedChannelOptions? dataChannelOptions = null)
    {
        Name = name ?? GetType().Name;
        var opts = dataChannelOptions ?? new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        };
        _dataChannel = Channel.CreateBounded<Frame>(opts);
    }

    /// <summary>Total frames currently queued. Useful for backpressure metrics.</summary>
    public int QueuedFrameCount => _systemChannel.Reader.Count + _dataChannel.Reader.Count;

    /// <summary>
    /// Enqueue a frame for this processor. System frames go to the priority channel;
    /// everything else to the bounded data channel.
    /// </summary>
    public ValueTask QueueFrameAsync(Frame frame, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var channel = frame is SystemFrame ? _systemChannel : _dataChannel;
        return channel.Writer.WriteAsync(frame, ct);
    }

    /// <summary>Begin draining channels. Idempotent guard: throws if called twice.</summary>
    public void Start(CancellationToken externalCt = default)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            throw new InvalidOperationException($"FrameProcessor '{Name}' is already started.");
        }

        var linked = CancellationTokenSource.CreateLinkedTokenSource(_processorCts.Token, externalCt);
        var token = linked.Token;
        _systemTask = Task.Run(() => RunSystemLoopAsync(token), token);
        _dataTask = Task.Run(() => RunDataLoopAsync(token), token);
    }

    /// <summary>
    /// Wire this processor's downstream output to <paramref name="next"/> and <paramref name="next"/>'s
    /// upstream output back to this processor's input. Call once per pair.
    /// </summary>
    public void Link(FrameProcessor next)
    {
        ArgumentNullException.ThrowIfNull(next);
        _downstream = (f, ct) => next.QueueFrameAsync(f, ct);
        next._upstream = (f, ct) => QueueFrameAsync(f, ct);
    }

    /// <summary>Override to transform frames. Always call <see cref="PushFrameAsync"/> to forward.</summary>
    protected abstract ValueTask ProcessFrameAsync(Frame frame, CancellationToken ct);

    /// <summary>
    /// Forward a frame to the next processor in its <see cref="Frame.Direction"/>. Frames with no
    /// link in that direction are dropped silently — that's the boundary case at the source/sink.
    /// </summary>
    protected ValueTask PushFrameAsync(Frame frame, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var dest = frame.Direction == FrameDirection.Downstream ? _downstream : _upstream;
        return dest?.Invoke(frame, ct) ?? ValueTask.CompletedTask;
    }

    /// <summary>Convenience: emit an upstream-direction <see cref="ErrorFrame"/>.</summary>
    protected ValueTask PushErrorAsync(string message, Exception? inner = null, CancellationToken ct = default)
        => PushFrameAsync(new ErrorFrame(message, inner) { Direction = FrameDirection.Upstream }, ct);

    /// <summary>Override to react to <see cref="StartFrame"/>. Called before <see cref="ProcessFrameAsync"/>.</summary>
    protected virtual ValueTask OnStartAsync(StartFrame frame, CancellationToken ct) => ValueTask.CompletedTask;

    /// <summary>Override to react to <see cref="EndFrame"/>. Called before <see cref="ProcessFrameAsync"/>.</summary>
    protected virtual ValueTask OnEndAsync(EndFrame frame, CancellationToken ct) => ValueTask.CompletedTask;

    /// <summary>Override to react to <see cref="InterruptionFrame"/>. Called from the system task.</summary>
    protected virtual ValueTask OnInterruptionAsync(InterruptionFrame frame, CancellationToken ct) => ValueTask.CompletedTask;

    private async Task RunSystemLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _systemChannel.Reader.ReadAllAsync(ct))
            {
                if (frame is InterruptionFrame interruption)
                {
                    // Preempt: cancel any in-flight data frame so long-running calls abort.
                    _currentFrameCts?.Cancel();
                    await OnInterruptionAsync(interruption, ct).ConfigureAwait(false);
                }
                await ProcessFrameAsync(frame, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (ChannelClosedException) { /* shutdown */ }
    }

    private async Task RunDataLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _dataChannel.Reader.ReadAllAsync(ct))
            {
                using var frameCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _currentFrameCts = frameCts;
                try
                {
                    switch (frame)
                    {
                        case StartFrame s: await OnStartAsync(s, frameCts.Token).ConfigureAwait(false); break;
                        case EndFrame e: await OnEndAsync(e, frameCts.Token).ConfigureAwait(false); break;
                    }
                    await ProcessFrameAsync(frame, frameCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (frameCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    // Frame was preempted by an interruption — that's fine, drop and continue.
                }
                finally
                {
                    _currentFrameCts = null;
                }

                if (frame is EndFrame) break;
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (ChannelClosedException) { /* shutdown */ }
    }

    public async ValueTask DisposeAsync()
    {
        _processorCts.Cancel();
        _systemChannel.Writer.TryComplete();
        _dataChannel.Writer.TryComplete();

        try
        {
            if (_systemTask is not null) await _systemTask.ConfigureAwait(false);
            if (_dataTask is not null) await _dataTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }

        _processorCts.Dispose();
        GC.SuppressFinalize(this);
    }
}
