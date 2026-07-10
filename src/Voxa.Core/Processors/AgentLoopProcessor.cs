using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Voxa.Diagnostics;
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
///   <item>Background-result turns (VDX-008): a consumed <see cref="BackgroundTaskCompletedFrame"/>
///       re-enters as an ordinary turn with <see cref="VoiceTurnContext.Trigger"/> =
///       <see cref="TurnTrigger.BackgroundResult"/>, held while an utterance is in flight and
///       released data-ordered behind that utterance's turn (<see cref="BackgroundResultOptions"/>).</item>
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
    private readonly BackgroundResultOptions _backgroundResults;
    private readonly VoxaDiagnosticsHub? _diagnosticsHub;

    /// <summary>A queued turn: user text, or a background-task result re-entering the conversation.</summary>
    private readonly record struct TurnRequest(string UserText, BackgroundTaskCompletedFrame? BackgroundResult)
    {
        public TurnTrigger Trigger => BackgroundResult is null ? TurnTrigger.UserUtterance : TurnTrigger.BackgroundResult;
    }

    private readonly Channel<TurnRequest> _turnQueue = Channel.CreateUnbounded<TurnRequest>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    // Held background results (VDX-008 §4.1). Mutated from BOTH loops: speaking edges arrive on the
    // system task, completed-task frames and finals on the data task — every touch goes through the lock.
    private readonly object _holdLock = new();
    private readonly List<BackgroundTaskCompletedFrame> _heldResults = new();
    private bool _holdingForUtterance;
    private bool _userSpeaking;            // live edge state — distinct from _holdingForUtterance, which persists until a release
    private bool _finalSeenWhileSpeaking;  // a final consumed mid-speech defers its release to the stop edge
    private int _holdGeneration;

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
    /// <param name="backgroundResults">Arbitration knobs for background-result turns (VDX-008 §4.1).
    /// Null ⇒ defaults (hold while the user speaks, 4 pending max, 2 s quiet-timeout).</param>
    /// <param name="diagnosticsHub">Optional hub for <see cref="BackgroundTaskDroppedEvent"/>s when the
    /// pending cap evicts a held result. Null ⇒ no diagnostics (the default composition wires it).</param>
    public AgentLoopProcessor(
        IAgentTurnDriver driver,
        Func<VoiceTurnContext, CancellationToken, ValueTask>? onTurnStarted = null,
        Func<VoiceTurnContext, TurnSummary, CancellationToken, ValueTask>? onTurnCompleted = null,
        Func<VoiceTurnContext, Exception, CancellationToken, ValueTask>? onTurnFailed = null,
        Func<VoiceTurnContext>? contextFactory = null,
        TimeSpan? maxResponseDuration = null,
        BackgroundResultOptions? backgroundResults = null,
        VoxaDiagnosticsHub? diagnosticsHub = null)
        : base(name: "AgentLoop")
    {
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));
        _onTurnStarted = onTurnStarted;
        _onTurnCompleted = onTurnCompleted;
        _onTurnFailed = onTurnFailed;
        _contextFactory = contextFactory;
        _maxResponseDuration = maxResponseDuration;
        _backgroundResults = backgroundResults ?? new BackgroundResultOptions();
        // Fail at construction, not mid-session: MaxPendingResults = 0 would make the drop-oldest
        // eviction index an empty list and fault the data loop (PR #96 review).
        ArgumentOutOfRangeException.ThrowIfLessThan(_backgroundResults.MaxPendingResults, 1, nameof(backgroundResults));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            _backgroundResults.HeldResultReleaseTimeout, TimeSpan.Zero, nameof(backgroundResults));
        _diagnosticsHub = diagnosticsHub;
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
                await _turnQueue.Writer.WriteAsync(new TurnRequest(t.Text, null), ct).ConfigureAwait(false);
                // Data-ordered release (VDX-008 §4.1): held background results go out immediately
                // BEHIND the turn this final just triggered — both writes happen on the data loop,
                // so no result turn can overtake the user's utterance.
                OnFinalTranscriptionConsumed();
                return;

            case TranscriptionFrame { IsFinal: true }:
                // Empty / whitespace final (VRT-002 WS2 §6.3): a silence misfire, a noise burst, or a Whisper
                // hallucination the filter dropped. Do NOT enqueue a turn — forward the frame so transports can
                // clear any "thinking" affordance, and leave the worker idle and ready so the next
                // UserStartedSpeakingFrame runs normally (no anticipatory state latched waiting on a turn that
                // will never come — speech-core's stuck-pipeline guard).
                await PushFrameAsync(frame, ct).ConfigureAwait(false);
                // No turn will follow this utterance — release held results now (VDX-008 §4.1).
                OnFinalTranscriptionConsumed();
                return;

            case ToolCallResultFrame r:
                if (_pendingFrontendTools.TryRemove(r.CallId, out var pending))
                {
                    pending.TrySetResult(r);
                }
                // Do NOT forward this frame downstream — it's consumed by the agent loop. Forwarding
                // would push a "the user just got a tool result" envelope to the sink for no reason.
                return;

            case BackgroundTaskCompletedFrame completed:
                // Consumed, never forwarded further upstream — the loop is this frame's destination
                // (mirrors ToolCallResultFrame). Held while an utterance is in flight so the result
                // turn can't talk over the user (VDX-008 §4.1).
                HoldOrEnqueueBackgroundResult(completed);
                return;

            case UserStartedSpeakingFrame:
                // Arrives on the SYSTEM loop — only observe (under the hold lock) and forward.
                OnUserStartedSpeaking();
                await PushFrameAsync(frame, ct).ConfigureAwait(false);
                return;

            case UserStoppedSpeakingFrame:
                // Stop-speaking must NOT release held results — the STT stage forwards it before the
                // promoted transcript, and as a system frame it can overtake queued finals. It only
                // arms the quiet-timeout fallback for utterances whose final never arrives.
                OnUserStoppedSpeaking();
                await PushFrameAsync(frame, ct).ConfigureAwait(false);
                return;

            default:
                await PushFrameAsync(frame, ct).ConfigureAwait(false);
                return;
        }
    }

    // ── Background-result arbitration (VDX-008 §4.1) ─────────────────────────────────────────

    private void HoldOrEnqueueBackgroundResult(BackgroundTaskCompletedFrame completed)
    {
        lock (_holdLock)
        {
            if (_backgroundResults.HoldWhileUserSpeaking && _holdingForUtterance)
            {
                if (_heldResults.Count >= _backgroundResults.MaxPendingResults)
                {
                    var dropped = _heldResults[0];
                    _heldResults.RemoveAt(0);
                    if (_diagnosticsHub is { HasListeners: true } hub)
                    {
                        hub.Publish(new BackgroundTaskDroppedEvent(dropped.TaskId));
                    }
                }
                _heldResults.Add(completed);
                return;
            }
        }
        _turnQueue.Writer.TryWrite(new TurnRequest(string.Empty, completed));
    }

    private void OnUserStartedSpeaking()
    {
        lock (_holdLock)
        {
            _userSpeaking = true;
            _holdingForUtterance = true;
            _finalSeenWhileSpeaking = false;
            // Invalidate any quiet-timeout armed by a previous stop-speaking.
            _holdGeneration++;
        }
    }

    /// <summary>
    /// A final was consumed on the data loop (its user turn, if any, is already enqueued). Release
    /// held results now — unless the edges say the user is speaking, which means either the stop
    /// edge is still queued cross-channel or this is a streaming/late final mid-utterance. In both
    /// cases result turns must not be injected while the user talks: defer to the stop edge, which
    /// releases immediately when a final has already been seen (PR #96 review).
    /// </summary>
    private void OnFinalTranscriptionConsumed()
    {
        lock (_holdLock)
        {
            if (_userSpeaking)
            {
                _finalSeenWhileSpeaking = true;
                return;
            }
        }
        ReleaseHeldResults();
    }

    private void OnUserStoppedSpeaking()
    {
        bool releaseNow;
        var generation = 0;
        lock (_holdLock)
        {
            _userSpeaking = false;
            releaseNow = _finalSeenWhileSpeaking;
            _finalSeenWhileSpeaking = false;
            if (!releaseNow)
            {
                if (!_holdingForUtterance) return;
                generation = _holdGeneration;
            }
        }

        if (releaseNow)
        {
            // The utterance's final already ran its turn — release right behind it.
            ReleaseHeldResults();
        }
        else
        {
            // No final yet: it usually arrives moments later (release happens there); the timer
            // covers utterances whose final never comes.
            _ = ReleaseAfterQuietTimeoutAsync(generation);
        }
    }

    /// <summary>Fallback for utterances whose final transcription never arrives (VDX-008 §4.1).</summary>
    private async Task ReleaseAfterQuietTimeoutAsync(int generation)
    {
        try
        {
            await Task.Delay(_backgroundResults.HeldResultReleaseTimeout, _processorCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // Shutdown — held results die with the session.
        }
        ReleaseHeldResults(generation);
    }

    /// <summary>
    /// Drain the held buffer into the turn queue. With a <paramref name="generation"/>, releases only
    /// if no newer utterance started since the timeout was armed (a final consumed on the data loop
    /// releases unconditionally — it IS the ordering signal).
    /// </summary>
    private void ReleaseHeldResults(int? generation = null)
    {
        BackgroundTaskCompletedFrame[] toRelease;
        lock (_holdLock)
        {
            if (generation is { } g && (g != _holdGeneration || !_holdingForUtterance)) return;
            // Defense-in-depth: every caller already checks the speaking state, but a release must
            // never drain while the user talks (PR #96 review).
            if (generation is null && _userSpeaking) return;
            _holdingForUtterance = false;
            _holdGeneration++;
            if (_heldResults.Count == 0) return;
            toRelease = _heldResults.ToArray();
            _heldResults.Clear();
        }
        foreach (var result in toRelease)
        {
            _turnQueue.Writer.TryWrite(new TurnRequest(string.Empty, result));
        }
    }

    private async Task RunTurnsAsync(CancellationToken ct)
    {
        var gateway = new InternalFrontendToolGateway(this);
        var emitter = new InternalFrameEmitter(this);

        try
        {
            await foreach (var request in _turnQueue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var turnId = Guid.NewGuid().ToString("N");
                var ctx = BuildTurnContext(turnId, request, gateway, emitter);

                var stopwatch = Stopwatch.StartNew();
                var assistantText = new StringBuilder();
                var pendingForThisTurn = new List<string>();
                long inputTokens = 0;
                long outputTokens = 0;

                try
                {
                    await PushFrameAsync(new LlmTurnStartedFrame(turnId, request.Trigger), ct).ConfigureAwait(false);

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
                        await PushFrameAsync(new LlmTurnEndedFrame(turnId, request.Trigger), ct).ConfigureAwait(false);
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
        TurnRequest request,
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
                UserText = request.UserText,
                FrontendTools = gateway,
                Emitter = emitter,
                Trigger = request.Trigger,
                BackgroundResult = request.BackgroundResult,
                Metadata = seeded.Metadata,
            };
        }

        return new VoiceTurnContext
        {
            TurnId = turnId,
            UserText = request.UserText,
            FrontendTools = gateway,
            Emitter = emitter,
            Trigger = request.Trigger,
            BackgroundResult = request.BackgroundResult,
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

    /// <summary>Binary-compat shim: preserves the public <c>DisposeAsync</c> member this sealed type used to
    /// declare, forwarding to the base whose <see cref="DisposeAsyncCore"/> hook performs the actual cleanup.</summary>
    // A precompiled consumer may hold a member reference to AgentLoopProcessor.DisposeAsync; keeping the public
    // member lets it bind after a Voxa.Core upgrade without recompiling. Unlike the original `new` method it carries
    // NO cleanup logic (that lives in DisposeAsyncCore), so cleanup runs exactly once on every dispose path.
    public new ValueTask DisposeAsync() => base.DisposeAsync();

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
