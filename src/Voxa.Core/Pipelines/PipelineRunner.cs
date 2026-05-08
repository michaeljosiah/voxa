using Voxa.Frames;

namespace Voxa.Pipelines;

/// <summary>
/// Owns the lifecycle of a <see cref="Pipeline"/> for one session: starts every processor's drain
/// loops, injects the initial <see cref="StartFrame"/>, watches the sink for <see cref="EndFrame"/>
/// and the source's upstream channel for <see cref="ErrorFrame"/>, and triggers ordered shutdown.
/// </summary>
public sealed class PipelineRunner : IAsyncDisposable
{
    private readonly Pipeline _pipeline;
    private readonly CancellationTokenSource _cts;
    private readonly TaskCompletionSource<object?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task? _sinkWatcher;
    private Task? _upstreamWatcher;
    private int _started;
    private int _disposed;

    public PipelineRunner(Pipeline pipeline, CancellationToken externalCt = default)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
    }

    /// <summary>
    /// Start every processor's drain loops and inject <paramref name="startFrame"/> (or a default one)
    /// at the source. Idempotent guard: throws if called twice.
    /// </summary>
    public async Task StartAsync(StartFrame? startFrame = null, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            throw new InvalidOperationException("PipelineRunner is already started.");
        }

        foreach (var p in _pipeline.Processors)
        {
            p.Start(_cts.Token);
        }

        _sinkWatcher = Task.Run(WatchSinkAsync, _cts.Token);
        _upstreamWatcher = Task.Run(WatchUpstreamAsync, _cts.Token);

        await _pipeline.Source.IngestAsync(startFrame ?? new StartFrame(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Inject an <see cref="EndFrame"/> at the source and wait up to <paramref name="gracePeriod"/>
    /// for it to flush through. After the grace window, hard-cancel.
    /// </summary>
    public async Task StopAsync(TimeSpan? gracePeriod = null, CancellationToken ct = default)
    {
        if (_started == 0) return;

        try
        {
            await _pipeline.Source.IngestAsync(new EndFrame(), ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // If we can't ingest, fall through to cancel.
        }

        var grace = gracePeriod ?? TimeSpan.FromSeconds(5);
        var graceTask = Task.Delay(grace, ct);
        var winner = await Task.WhenAny(_completion.Task, graceTask).ConfigureAwait(false);
        if (winner != _completion.Task)
        {
            _cts.Cancel();
        }
    }

    /// <summary>
    /// Completes when the pipeline ends (<see cref="EndFrame"/> reaches the sink) or fails
    /// (an <see cref="ErrorFrame"/> reaches the source upstream). Awaiting throws on failure.
    /// </summary>
    public Task WaitAsync() => _completion.Task;

    private async Task WatchSinkAsync()
    {
        try
        {
            await _pipeline.Sink.EndFrameObserved.WaitAsync(_cts.Token).ConfigureAwait(false);
            _completion.TrySetResult(null);
        }
        catch (OperationCanceledException)
        {
            _completion.TrySetCanceled();
        }
        catch (Exception ex)
        {
            _completion.TrySetException(ex);
        }
    }

    private async Task WatchUpstreamAsync()
    {
        try
        {
            await foreach (var frame in _pipeline.Source.ReadUpstreamAsync(_cts.Token).ConfigureAwait(false))
            {
                if (frame is ErrorFrame err)
                {
                    _completion.TrySetException(new PipelineFailedException(err));
                    _cts.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _completion.TrySetException(ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        _cts.Cancel();

        foreach (var p in _pipeline.Processors)
        {
            await p.DisposeAsync().ConfigureAwait(false);
        }

        try { if (_sinkWatcher is not null) await _sinkWatcher.ConfigureAwait(false); } catch { }
        try { if (_upstreamWatcher is not null) await _upstreamWatcher.ConfigureAwait(false); } catch { }

        _cts.Dispose();
    }
}
