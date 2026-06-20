using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Voxa.Frames;

namespace Voxa.Processors;

/// <summary>
/// Generic per-turn agent processor. Drains a transcription queue on a background turn worker (so
/// the data loop is never blocked by host code), invokes an <see cref="IAgentTurnDriver"/> per turn,
/// pumps yielded frames downstream, and bridges frontend-tool round-trips via
/// <see cref="IFrontendToolGateway"/>.
///
/// <para>
/// Framework-agnostic — knows nothing about Microsoft Agent Framework, Semantic Kernel, or any
/// specific agent runtime. The driver supplies the per-turn behavior; this processor owns the
/// pipeline-bookkeeping concerns that every voice-agent integration would otherwise re-implement:
/// </para>
/// <list type="bullet">
///   <item>Data-loop / turn-worker split (deadlock-safe).</item>
///   <item>Per-turn id, lifecycle frames (<see cref="LlmTurnStartedFrame"/> /
///       <see cref="LlmTurnEndedFrame"/>), per-turn stopwatch + assistant-text accumulation.</item>
///   <item>Frontend-tool TCS correlation (continuations run async; data loop never blocks).</item>
///   <item>Per-turn try/catch isolation: a failed turn emits an upstream <see cref="ErrorFrame"/>
///       and the worker continues to drain the next queued transcription.</item>
///   <item>Clean shutdown on <see cref="EndFrame"/> / connection cancellation: turn queue completed,
///       pending TCSs cancelled, worker joined.</item>
/// </list>
/// </summary>
public sealed class AgentLoopProcessor : FrameProcessor
{
    private readonly IAgentTurnDriver _driver;
    private readonly Func<VoiceTurnContext, CancellationToken, ValueTask>? _onTurnStarted;
    private readonly Func<VoiceTurnContext, TurnSummary, CancellationToken, ValueTask>? _onTurnCompleted;
    private readonly Func<VoiceTurnContext, Exception, CancellationToken, ValueTask>? _onTurnFailed;
    private readonly Func<VoiceTurnContext>? _contextFactory;
    private readonly TimeSpan? _maxResponseDuration;

    private readonly Channel<string> _turnQueue = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly ConcurrentDictionary<string, TaskCompletionSource<ToolCallResultFrame>> _pendingFrontendTools
        = new(StringComparer.Ordinal);

    private readonly CancellationTokenSource _processorCts = new();
    private Task? _turnWorker;

    /// <summary>
    /// Construct an agent-loop processor. Hosts that need turn-level hooks (notification, audit,
    /// metadata enrichment) supply them here; the rest of the work happens inside the supplied
    /// <paramref name="driver"/>.
    /// </summary>
    /// <param name="driver">Per-turn agent driver (e.g. the MAF adapter).</param>
    /// <param name="onTurnStarted">Optional hook fired after <see cref="LlmTurnStartedFrame"/>, before <c>RunTurnAsync</c>.</param>
    /// <param name="onTurnCompleted">Optional hook fired with the <see cref="TurnSummary"/> after a successful turn.</param>
    /// <param name="onTurnFailed">Optional hook fired when the driver throws. Default behavior emits an upstream <see cref="ErrorFrame"/>.</param>
    /// <param name="contextFactory">
    /// Optional factory that builds an initial <see cref="VoiceTurnContext"/>. Lets hosts pre-populate
    /// the metadata bag with per-connection values (e.g. <c>Hello</c>, <c>ClaimsPrincipal</c>) before
    /// the driver sees the context. The processor still sets <see cref="VoiceTurnContext.TurnId"/>,
    /// <see cref="VoiceTurnContext.UserText"/>, <see cref="VoiceTurnContext.FrontendTools"/>, and
    /// <see cref="VoiceTurnContext.Emitter"/> — those fields are owned by the loop.
    /// </param>
    /// <param name="maxResponseDuration">
    /// Optional response-duration cap (VRT-002 WS2 §6.5). When set, a single turn stops pumping the driver's
    /// yielded frames once its wall-clock elapsed time reaches this bound, then closes the turn cleanly
    /// (<see cref="LlmTurnEndedFrame"/> still fires; <paramref name="onTurnCompleted"/> still runs). A capped
    /// turn is a normal truncated completion, not an error. Null ⇒ no cap (default). Guards against a looping
    /// LLM or runaway TTS talking over the user's next turns.
    /// </param>
    public AgentLoopProcessor(
        IAgentTurnDriver driver,
        Func<VoiceTurnContext, CancellationToken, ValueTask>? onTurnStarted = null,
        Func<VoiceTurnContext, TurnSummary, CancellationToken, ValueTask>? onTurnCompleted = null,
        Func<VoiceTurnContext, Exception, CancellationToken, ValueTask>? onTurnFailed = null,
        Func<VoiceTurnContext>? contextFactory = null,
        TimeSpan? maxResponseDuration = null)
        : base(name: "AgentLoop")
    {
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));
        _onTurnStarted = onTurnStarted;
        _onTurnCompleted = onTurnCompleted;
        _onTurnFailed = onTurnFailed;
        _contextFactory = contextFactory;
        _maxResponseDuration = maxResponseDuration;
    }

    protected override ValueTask OnStartAsync(StartFrame frame, CancellationToken ct)
    {
        _turnWorker = Task.Run(() => RunTurnsAsync(_processorCts.Token), _processorCts.Token);
        return ValueTask.CompletedTask;
    }

    protected override async ValueTask OnEndAsync(EndFrame frame, CancellationToken ct)
    {
        _turnQueue.Writer.TryComplete();

        // Cancel any pending frontend-tool TCSs so the worker doesn't hang on a never-arriving result.
        foreach (var pending in _pendingFrontendTools.Values)
        {
            pending.TrySetCanceled(ct);
        }
        _pendingFrontendTools.Clear();

        if (_turnWorker is not null)
        {
            try
            {
                await _turnWorker.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _processorCts.Cancel();
            }
            catch (OperationCanceledException)
            {
                // Graceful — connection cancelled.
            }
        }
    }

    /// <summary>
    /// Data loop — fire-and-forget routing only. Returns within microseconds for every frame,
    /// regardless of agent or tool state. Awaiting handler work here would deadlock the channel
    /// (the next frame can't be read until this one's awaited <c>ProcessFrameAsync</c> returns).
    /// </summary>
    protected override async ValueTask ProcessFrameAsync(Frame frame, CancellationToken ct)
    {
        switch (frame)
        {
            case TranscriptionFrame { IsFinal: true } t when !string.IsNullOrWhiteSpace(t.Text):
                // Forward the transcription so transports can render the user bubble immediately.
                await PushFrameAsync(frame, ct).ConfigureAwait(false);
                // Hand the user text off to the background turn worker.
                await _turnQueue.Writer.WriteAsync(t.Text, ct).ConfigureAwait(false);
                return;

            case TranscriptionFrame { IsFinal: true }:
                // Empty / whitespace final (VRT-002 WS2 §6.3): a silence misfire, a noise burst, or a Whisper
                // hallucination the filter dropped. Do NOT enqueue a turn — forward the frame so transports can
                // clear any "thinking" affordance, and leave the worker idle and ready so the next
                // UserStartedSpeakingFrame runs normally (no anticipatory state latched waiting on a turn that
                // will never come — speech-core's stuck-pipeline guard).
                await PushFrameAsync(frame, ct).ConfigureAwait(false);
                return;

            case ToolCallResultFrame r:
                if (_pendingFrontendTools.TryRemove(r.CallId, out var pending))
                {
                    pending.TrySetResult(r);
                }
                // Do NOT forward this frame downstream — it's consumed by the agent loop. Forwarding
                // would push a "the user just got a tool result" envelope to the sink for no reason.
                return;

            default:
                await PushFrameAsync(frame, ct).ConfigureAwait(false);
                return;
        }
    }

    private async Task RunTurnsAsync(CancellationToken ct)
    {
        var gateway = new InternalFrontendToolGateway(this);
        var emitter = new InternalFrameEmitter(this);

        try
        {
            await foreach (var userText in _turnQueue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var turnId = Guid.NewGuid().ToString("N");
                var ctx = BuildTurnContext(turnId, userText, gateway, emitter);

                var stopwatch = Stopwatch.StartNew();
                var assistantText = new StringBuilder();
                var pendingForThisTurn = new List<string>();
                long inputTokens = 0;
                long outputTokens = 0;

                try
                {
                    await PushFrameAsync(new LlmTurnStartedFrame(turnId), ct).ConfigureAwait(false);

                    if (_onTurnStarted is not null)
                    {
                        await _onTurnStarted.Invoke(ctx, ct).ConfigureAwait(false);
                    }

                    // Response-duration cap (VRT-002 WS2 §6.5): a linked CTS cancels the driver enumeration
                    // when the wall-clock cap is hit, so a turn that stalls before its first chunk, pauses
                    // longer than the cap between chunks, or blocks on a frontend tool is bounded too — not
                    // only one that keeps yielding frames. A capped turn is a normal truncated completion: the
                    // cancellation is swallowed below and the summary + LlmTurnEndedFrame (finally) still run.
                    CancellationTokenSource? capCts = null;
                    if (_maxResponseDuration is { } cap)
                    {
                        capCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        capCts.CancelAfter(cap);
                    }
                    var driverToken = capCts?.Token ?? ct;
                    try
                    {
                        await foreach (var frame in _driver.RunTurnAsync(ctx, driverToken).ConfigureAwait(false))
                        {
                            // Secondary guard: a driver that yields rapidly without ever observing the token
                            // would otherwise outrun the cancellation between its awaits.
                            if (_maxResponseDuration is { } c && stopwatch.Elapsed >= c) break;

                            switch (frame)
                            {
                                case LlmTextChunkFrame chunk:
                                    assistantText.Append(chunk.Text);
                                    break;
                                case ToolCallRequestFrame call:
                                    pendingForThisTurn.Add(call.CallId);
                                    break;
                                case LlmUsageFrame usage:
                                    // Aggregate-only — usage frames are loop bookkeeping, not transport
                                    // output. Hosts read totals via TurnSummary.Usage in OnTurnCompleted.
                                    inputTokens += usage.InputTokens;
                                    outputTokens += usage.OutputTokens;
                                    continue;
                            }
                            // Forward on the turn token, not the cap token: an already-yielded frame still ships.
                            await PushFrameAsync(frame, ct).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) when (capCts is { IsCancellationRequested: true } && !ct.IsCancellationRequested)
                    {
                        // Response-duration cap fired mid-generation — truncate cleanly and fall through to the summary.
                    }
                    finally
                    {
                        capCts?.Dispose();
                    }

                    var summary = new TurnSummary(
                        TurnId: turnId,
                        AssistantText: assistantText.ToString(),
                        ElapsedMs: stopwatch.ElapsedMilliseconds,
                        Usage: new UsageTotals(inputTokens, outputTokens));

                    if (_onTurnCompleted is not null)
                    {
                        await _onTurnCompleted.Invoke(ctx, summary, ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Connection cancelled — drop the turn and exit cleanly.
                    CancelPending(pendingForThisTurn);
                    break;
                }
                catch (Exception ex)
                {
                    // Per-turn isolation: emit an upstream ErrorFrame and continue draining the queue.
                    CancelPending(pendingForThisTurn);
                    if (_onTurnFailed is not null)
                    {
                        try
                        {
                            await _onTurnFailed.Invoke(ctx, ex, ct).ConfigureAwait(false);
                        }
                        catch
                        {
                            // Best-effort hook — never let a hook failure mask the original error.
                        }
                    }
                    try
                    {
                        await PushErrorAsync($"Voice turn failed: {ex.Message}", ex, ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Sink may be torn down already.
                    }
                }
                finally
                {
                    try
                    {
                        await PushFrameAsync(new LlmTurnEndedFrame(turnId), ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Best-effort; sink may be gone.
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (ChannelClosedException) { /* shutdown */ }
    }

    private VoiceTurnContext BuildTurnContext(
        string turnId,
        string userText,
        IFrontendToolGateway gateway,
        IFrameEmitter emitter)
    {
        if (_contextFactory is not null)
        {
            var seeded = _contextFactory();
            // Force-set the loop-owned fields. The host can pre-populate Metadata.
            return new VoiceTurnContext
            {
                TurnId = turnId,
                UserText = userText,
                FrontendTools = gateway,
                Emitter = emitter,
                Metadata = seeded.Metadata,
            };
        }

        return new VoiceTurnContext
        {
            TurnId = turnId,
            UserText = userText,
            FrontendTools = gateway,
            Emitter = emitter,
        };
    }

    private void CancelPending(IEnumerable<string> callIds)
    {
        foreach (var callId in callIds)
        {
            if (_pendingFrontendTools.TryRemove(callId, out var tcs))
            {
                tcs.TrySetCanceled();
            }
        }
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        // Runs on every disposal path — including PipelineRunner's base-typed dispose, which the former
        // `new DisposeAsync` silently skipped (CQ-001), leaking the turn worker + its CTS on abrupt
        // teardown (no EndFrame). Idempotent: safe whether or not OnEndAsync already drained the worker.
        _turnQueue.Writer.TryComplete();
        _processorCts.Cancel();

        // Cancel pending frontend-tool waits BEFORE awaiting the worker: a driver blocked in
        // AwaitToolResultAsync on CancellationToken.None is released only by cancelling its TCS, not by
        // the processor CTS — awaiting first would deadlock disposal. (Mirrors OnEndAsync's ordering.)
        foreach (var pending in _pendingFrontendTools.Values) pending.TrySetCanceled();
        _pendingFrontendTools.Clear();

        if (_turnWorker is not null)
        {
            // Bound the join (mirrors OnEndAsync): a driver stuck in work that never observes the processor
            // token must not hang PipelineRunner.DisposeAsync forever — give up after the timeout and let the
            // stuck task leak rather than blocking connection cleanup indefinitely (CQ-001, codex P2).
            try { await _turnWorker.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (TimeoutException) { /* driver ignored cancellation; don't block teardown on it */ }
            catch (OperationCanceledException) { /* worker observed the cancel during teardown */ }
            _turnWorker = null;
        }

        _processorCts.Dispose();
        await base.DisposeAsyncCore().ConfigureAwait(false);
    }

    // ── Internal gateway/emitter — exposed to the driver via VoiceTurnContext ────────────────

    private sealed class InternalFrontendToolGateway : IFrontendToolGateway
    {
        private readonly AgentLoopProcessor _owner;

        public InternalFrontendToolGateway(AgentLoopProcessor owner) => _owner = owner;

        public ValueTask<ToolCallResultFrame> AwaitToolResultAsync(string callId, CancellationToken ct)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(callId);

            // RunContinuationsAsynchronously is critical: when ProcessFrameAsync (data loop) calls
            // tcs.TrySetResult(...), the awaiting continuation MUST run on a worker thread, not
            // inline on the data-loop frame-pump. Otherwise a slow handler continuation would block
            // the next frame.
            var tcs = new TaskCompletionSource<ToolCallResultFrame>(TaskCreationOptions.RunContinuationsAsynchronously);

            // First registration wins — duplicate callIds are a host bug; we surface it loudly here
            // rather than silently overwriting.
            if (!_owner._pendingFrontendTools.TryAdd(callId, tcs))
            {
                throw new InvalidOperationException(
                    $"Duplicate frontend-tool callId '{callId}' awaited; tool calls must use unique ids.");
            }

            // Honor cancellation: if ct fires before the result arrives, remove the registration
            // and complete the task as cancelled.
            var registration = ct.Register(() =>
            {
                if (_owner._pendingFrontendTools.TryRemove(callId, out var pending))
                {
                    pending.TrySetCanceled(ct);
                }
            });

            return new ValueTask<ToolCallResultFrame>(WaitAndCleanupAsync(tcs.Task, registration));
        }

        private static async Task<ToolCallResultFrame> WaitAndCleanupAsync(
            Task<ToolCallResultFrame> task,
            CancellationTokenRegistration registration)
        {
            try { return await task.ConfigureAwait(false); }
            finally { registration.Dispose(); }
        }
    }

    private sealed class InternalFrameEmitter : IFrameEmitter
    {
        private readonly AgentLoopProcessor _owner;

        public InternalFrameEmitter(AgentLoopProcessor owner) => _owner = owner;

        public ValueTask EmitAsync(Frame frame, CancellationToken ct) => _owner.PushFrameAsync(frame, ct);
    }
}
