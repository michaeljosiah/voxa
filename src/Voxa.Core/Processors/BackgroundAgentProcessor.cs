using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Voxa.Diagnostics;
using Voxa.Frames;

namespace Voxa.Processors;

/// <summary>
/// Hosts a heavyweight second <see cref="IAgentTurnDriver"/> off the voice-latency critical path
/// (VDX-008 §5). Consumes <see cref="BackgroundTaskRequestFrame"/>s, runs the driver on a bounded
/// worker pool, and pushes a <see cref="BackgroundTaskCompletedFrame"/> upstream so
/// <see cref="AgentLoopProcessor"/> re-enters the result as a relevance-gated turn. A transparent
/// pass-through for every other frame.
///
/// <para>
/// The driver's output is contained by whitelist so unknown speech-bearing frames fail closed:
/// <see cref="LlmTextChunkFrame"/> and <see cref="TextFrame"/> accumulate into the result (TTS
/// synthesizes <see cref="TextFrame"/>, so it must never pass); <see cref="StatusFrame"/> forwards
/// downstream for progress UI; <see cref="LlmUsageFrame"/> aggregates into token totals; everything
/// else is dropped. Background tasks survive <see cref="InterruptionFrame"/> (barge-in must not
/// orphan promised work — the request frame is <see cref="IUninterruptible"/> and workers run on
/// the processor-lifetime token) and are cancelled by <see cref="EndFrame"/>/disposal.
/// </para>
/// </summary>
public sealed class BackgroundAgentProcessor : FrameProcessor
{
    /// <summary>Metadata key carrying <see cref="BackgroundTaskRequestFrame.ContextJson"/> into the driver's turn.</summary>
    public const string ContextJsonMetadataKey = "BackgroundTaskContextJson";

    /// <summary>Metadata key carrying <see cref="BackgroundTaskRequestFrame.OriginTurnId"/> into the driver's turn.</summary>
    public const string OriginTurnIdMetadataKey = "BackgroundTaskOriginTurnId";

    private static readonly TimeSpan DefaultTaskTimeout = TimeSpan.FromSeconds(120);

    private readonly IAgentTurnDriver _driver;
    private readonly TimeSpan _taskTimeout;
    private readonly int _maxConcurrentTasks;
    private readonly VoxaDiagnosticsHub? _diagnosticsHub;

    // Waiting requests only — a worker removes an item before running it, so the channel count is
    // the queue depth. TryWrite-only: a full channel REJECTS (IsError completion), never blocks the
    // data loop and never silently drops a task the model verbally promised (VDX-008 §5).
    private readonly Channel<BackgroundTaskRequestFrame> _requests;

    private readonly CancellationTokenSource _processorCts = new();
    private Task[]? _workers;

    /// <param name="backgroundDriver">The heavyweight agent driver. Its text output is accumulated
    /// into <see cref="BackgroundTaskCompletedFrame.ResultText"/>, never spoken directly.</param>
    /// <param name="maxConcurrentTasks">Bounded worker pool per session.</param>
    /// <param name="maxQueuedRequests">Bound on waiting requests; excess rejected with an immediate
    /// <c>IsError</c> completion so the interaction model can recover conversationally.</param>
    /// <param name="taskTimeout">Per-task wall-clock cap; a timed-out task completes with
    /// <c>IsError</c>, not silence. Null ⇒ 120 s.</param>
    /// <param name="diagnosticsHub">Optional hub for task lifecycle events. Null ⇒ no diagnostics.</param>
    public BackgroundAgentProcessor(
        IAgentTurnDriver backgroundDriver,
        int maxConcurrentTasks = 2,
        int maxQueuedRequests = 8,
        TimeSpan? taskTimeout = null,
        VoxaDiagnosticsHub? diagnosticsHub = null)
        : base(name: "BackgroundAgent")
    {
        _driver = backgroundDriver ?? throw new ArgumentNullException(nameof(backgroundDriver));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentTasks, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxQueuedRequests, 1);
        _maxConcurrentTasks = maxConcurrentTasks;
        _taskTimeout = taskTimeout ?? DefaultTaskTimeout;
        _diagnosticsHub = diagnosticsHub;
        _requests = Channel.CreateBounded<BackgroundTaskRequestFrame>(new BoundedChannelOptions(maxQueuedRequests)
        {
            FullMode = BoundedChannelFullMode.Wait, // never hit: writes are TryWrite-only (reject on full)
            SingleWriter = false,
            SingleReader = maxConcurrentTasks == 1,
        });
    }

    protected override ValueTask OnStartAsync(StartFrame frame, CancellationToken ct)
    {
        var workers = new Task[_maxConcurrentTasks];
        for (var i = 0; i < workers.Length; i++)
        {
            workers[i] = Task.Run(() => RunWorkerAsync(_processorCts.Token), _processorCts.Token);
        }
        _workers = workers;
        return ValueTask.CompletedTask;
    }

    protected override async ValueTask OnEndAsync(EndFrame frame, CancellationToken ct)
    {
        // EndFrame cancels in-flight tasks (VDX-008 §3) — background work dies with the session.
        _requests.Writer.TryComplete();
        _processorCts.Cancel();
        await JoinWorkersAsync(ct).ConfigureAwait(false);
    }

    protected override async ValueTask ProcessFrameAsync(Frame frame, CancellationToken ct)
    {
        if (frame is BackgroundTaskRequestFrame request)
        {
            if (!_requests.Writer.TryWrite(request))
            {
                // Queue full (or session ending): the model verbally promised this work, so it must
                // be TOLD — an immediate error completion, not a silent drop (VDX-008 §5).
                if (_diagnosticsHub is { HasListeners: true } hub)
                {
                    hub.Publish(new BackgroundTaskRejectedEvent(request.TaskId));
                }
                await PushCompletionAsync(new BackgroundTaskCompletedFrame(
                    request.TaskId,
                    ResultText: "background task queue is full",
                    IsError: true,
                    OriginTurnId: request.OriginTurnId)).ConfigureAwait(false);
            }
            return; // Consumed either way — the request never travels further downstream.
        }

        await PushFrameAsync(frame, ct).ConfigureAwait(false);
    }

    private async Task RunWorkerAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var request in _requests.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await RunOneTaskAsync(request, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (ChannelClosedException) { /* shutdown */ }
    }

    private async Task RunOneTaskAsync(BackgroundTaskRequestFrame request, CancellationToken ct)
    {
        if (_diagnosticsHub is { HasListeners: true } startHub)
        {
            startHub.Publish(new BackgroundTaskStartedEvent(request.TaskId, request.Goal));
        }

        var stopwatch = Stopwatch.StartNew();
        var resultText = new StringBuilder();
        long inputTokens = 0;
        long outputTokens = 0;
        var isError = false;
        string? errorText = null;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_taskTimeout);

        var ctx = new VoiceTurnContext
        {
            TurnId = request.TaskId,
            UserText = request.Goal,
            FrontendTools = ThrowingFrontendToolGateway.Instance,
            Emitter = new StatusOnlyEmitter(this),
            Metadata = new Dictionary<string, object?>
            {
                [ContextJsonMetadataKey] = request.ContextJson,
                [OriginTurnIdMetadataKey] = request.OriginTurnId,
            },
        };

        try
        {
            await foreach (var frame in _driver.RunTurnAsync(ctx, timeoutCts.Token).ConfigureAwait(false))
            {
                switch (frame)
                {
                    case LlmTextChunkFrame chunk:
                        resultText.Append(chunk.Text);
                        break;
                    case TextFrame text:
                        // TextToSpeechProcessor synthesizes TextFrame — accumulate, never forward.
                        resultText.Append(text.Text);
                        break;
                    case LlmUsageFrame usage:
                        inputTokens += usage.InputTokens;
                        outputTokens += usage.OutputTokens;
                        break;
                    case StatusFrame status:
                        // The sanitized-progress channel is the one background output allowed downstream.
                        await PushFrameAsync(status, ct).ConfigureAwait(false);
                        break;
                    default:
                        // Whitelist containment: an unrecognized frame may be speakable — fail closed.
                        Debug.WriteLine($"BackgroundAgentProcessor: dropped {frame.GetType().Name} from background driver (task {request.TaskId}).");
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return; // Session shutdown — no completion; held state dies with the session.
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            isError = true;
            errorText = $"background task timed out after {_taskTimeout.TotalSeconds:0}s";
        }
        catch (Exception ex)
        {
            // Per-task isolation: a failed task reports as an error result the interaction model can
            // apologize for — it must not degrade the session (VDX-008 §5).
            isError = true;
            errorText = ex.Message;
        }

        var completion = new BackgroundTaskCompletedFrame(
            request.TaskId,
            ResultText: isError ? errorText ?? "background task failed" : resultText.ToString(),
            IsError: isError,
            ElapsedMs: stopwatch.ElapsedMilliseconds,
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            OriginTurnId: request.OriginTurnId);

        await PushCompletionAsync(completion).ConfigureAwait(false);

        VoxaMetrics.BackgroundTaskDurationMs.Record(stopwatch.Elapsed.TotalMilliseconds);
        if (_diagnosticsHub is { HasListeners: true } hub)
        {
            hub.Publish(new BackgroundTaskCompletedEvent(request.TaskId, isError, stopwatch.Elapsed.TotalMilliseconds));
        }
    }

    private ValueTask PushCompletionAsync(BackgroundTaskCompletedFrame completion)
        => PushFrameAsync(completion with { Direction = FrameDirection.Upstream });

    private async ValueTask JoinWorkersAsync(CancellationToken ct)
    {
        if (_workers is not { } workers) return;
        try
        {
            // Bounded join (the AgentLoopProcessor discipline): a driver that ignores cancellation
            // must not hang session teardown — give up and let the stuck task leak instead.
            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }
        catch (TimeoutException) { }
        catch (OperationCanceledException) { }
        _workers = null;
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        _requests.Writer.TryComplete();
        _processorCts.Cancel();
        await JoinWorkersAsync(CancellationToken.None).ConfigureAwait(false);
        _processorCts.Dispose();
        await base.DisposeAsyncCore().ConfigureAwait(false);
    }

    /// <summary>Frontend tools are a UI round-trip — a contradiction for delegated work nobody is watching.</summary>
    private sealed class ThrowingFrontendToolGateway : IFrontendToolGateway
    {
        public static readonly ThrowingFrontendToolGateway Instance = new();

        public ValueTask<ToolCallResultFrame> AwaitToolResultAsync(string callId, CancellationToken ct)
            => throw new InvalidOperationException(
                "Frontend tools are unavailable in background turns (VDX-008 §5); use backend tools inside the driver.");
    }

    /// <summary>Out-of-band emits obey the same containment whitelist as yielded frames.</summary>
    private sealed class StatusOnlyEmitter : IFrameEmitter
    {
        private readonly BackgroundAgentProcessor _owner;

        public StatusOnlyEmitter(BackgroundAgentProcessor owner) => _owner = owner;

        public ValueTask EmitAsync(Frame frame, CancellationToken ct)
        {
            if (frame is StatusFrame) return _owner.PushFrameAsync(frame, ct);
            Debug.WriteLine($"BackgroundAgentProcessor: dropped emitted {frame.GetType().Name} from background driver.");
            return ValueTask.CompletedTask;
        }
    }
}
