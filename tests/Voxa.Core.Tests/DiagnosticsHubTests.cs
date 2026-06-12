using Voxa.Diagnostics;
using Voxa.Frames;
using Voxa.Processors;

namespace Voxa.Core.Tests;

/// <summary>
/// VST-001 WS0 coverage: the diagnostics hub's zero-cost-unobserved contract (WS0-A1), its
/// drop-oldest backpressure behavior (WS0-A2), the stage-latency tracker's turn state machine
/// (WS0-A3), and the per-position tap → event mapping.
/// </summary>
public class DiagnosticsHubTests
{
    // ── WS0-A1: publish with no subscriber is a no-op ───────────────────────

    [Fact]
    public async Task Publish_With_No_Subscriber_Is_Invisible_To_A_Later_Subscriber()
    {
        var hub = new VoxaDiagnosticsHub();
        Assert.False(hub.HasListeners);

        // Published into the void — must not be buffered for future subscribers,
        // and must not advance the sequence they observe.
        hub.Publish(new TurnEvent(TurnEdge.UserStarted));
        hub.Publish(new TurnEvent(TurnEdge.UserStopped));

        var received = new List<DiagnosticEvent>();
        using var cts = new CancellationTokenSource();
        var reader = Task.Run(async () =>
        {
            await foreach (var e in hub.SubscribeAsync(cts.Token))
            {
                received.Add(e);
                if (received.Count == 1) cts.Cancel();
            }
        });

        // Wait for the subscription to attach, then publish one live event.
        while (!hub.HasListeners) await Task.Delay(5);
        hub.Publish(new TranscriptEvent("hello", IsFinal: true));

        await Awaited(reader);
        var e = Assert.Single(received);
        var transcript = Assert.IsType<TranscriptEvent>(e);
        Assert.Equal("hello", transcript.Text);
        Assert.Equal(1, transcript.SeqNo); // pre-subscription publishes never counted
    }

    [Fact]
    public async Task HasListeners_Tracks_Subscription_Lifetime()
    {
        var hub = new VoxaDiagnosticsHub();
        using var cts = new CancellationTokenSource();

        var reader = Task.Run(async () =>
        {
            await foreach (var _ in hub.SubscribeAsync(cts.Token)) { }
        });

        while (!hub.HasListeners) await Task.Delay(5);
        cts.Cancel();
        await Awaited(reader);
        Assert.False(hub.HasListeners);
    }

    // ── WS0-A2: a slow subscriber drops oldest, never blocks the publisher ──

    [Fact]
    public async Task Slow_Subscriber_Loses_Oldest_Events_And_Sees_The_Gap_In_SeqNo()
    {
        const int capacity = 8;
        const int burst = 100;
        var hub = new VoxaDiagnosticsHub(channelCapacity: capacity);
        using var cts = new CancellationTokenSource();

        // The reader parks INSIDE the loop after the first event — the subscription is attached
        // (its channel buffers and drops independently), but nothing drains during the burst.
        var received = new List<DiagnosticEvent>();
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = Task.Run(async () =>
        {
            await foreach (var e in hub.SubscribeAsync(cts.Token))
            {
                received.Add(e);
                if (received.Count == 1) { parked.SetResult(); await resume.Task; }
                if (e.SeqNo == burst + 1) cts.Cancel(); // the final publish arrived — done
            }
        });

        while (!hub.HasListeners) await Task.Delay(5);
        hub.Publish(new AgentDeltaEvent("first"));   // SeqNo 1 — wakes and parks the reader
        await parked.Task.WaitAsync(TimeSpan.FromSeconds(5));

        for (int i = 0; i < burst; i++)              // SeqNo 2..101 into a parked channel
            hub.Publish(new AgentDeltaEvent($"chunk-{i}"));
        resume.SetResult();
        await Awaited(reader);

        // Publisher never blocked (the burst loop completed synchronously above); the channel
        // retained at most its capacity, the survivors are the NEWEST events, and the drop is
        // visible to the consumer as a SeqNo gap.
        Assert.True(received.Count <= capacity + 2, $"retained {received.Count}, capacity {capacity}");
        Assert.Equal(burst + 1, received[^1].SeqNo);
        Assert.True(received[^1].SeqNo - received[0].SeqNo + 1 > received.Count, "expected a SeqNo gap");
    }

    // ── WS0-A3: stage tracker derives the waterfall from anchor events ──────

    [Fact]
    public async Task Turn_Anchors_Derive_Ordered_Stage_Latencies()
    {
        var hub = new VoxaDiagnosticsHub();
        var received = new List<DiagnosticEvent>();
        using var cts = new CancellationTokenSource();
        var reader = Task.Run(async () =>
        {
            await foreach (var e in hub.SubscribeAsync(cts.Token))
            {
                received.Add(e);
                if (e is StageLatencyEvent { Stage: "audio_out" }) cts.Cancel();
            }
        });
        while (!hub.HasListeners) await Task.Delay(5);

        // One full turn, in pipeline order.
        hub.Publish(new VadWindowEvent(0.9f, 0.05, Voiced: true, GateOpen: true));
        hub.Publish(new TurnEvent(TurnEdge.UserStopped));
        hub.Publish(new TranscriptEvent("ask me anything", IsFinal: true));
        hub.Publish(new AgentDeltaEvent("You said:"));
        hub.Publish(new TtsChunkEvent(Bytes: 3200, SampleRate: 16000));
        hub.Publish(new StageLatencyEvent("audio_out", 1.5)); // a sink publishing its own stage

        await Awaited(reader);

        var stages = received.OfType<StageLatencyEvent>().Select(s => s.Stage).ToList();
        Assert.Equal(["vad_close", "stt_final", "agent_first_token", "tts_first_byte", "audio_out"], stages);
        Assert.All(received.OfType<StageLatencyEvent>(), s => Assert.True(s.Ms >= 0));

        // Each derived stage rides the stream AFTER its anchor: e.g. stt_final follows the transcript.
        var transcriptSeq = received.OfType<TranscriptEvent>().Single().SeqNo;
        var sttStageSeq = received.OfType<StageLatencyEvent>().Single(s => s.Stage == "stt_final").SeqNo;
        Assert.True(sttStageSeq > transcriptSeq);
    }

    [Fact]
    public async Task Interruption_Abandons_The_Half_Measured_Turn()
    {
        var hub = new VoxaDiagnosticsHub();
        var received = new List<DiagnosticEvent>();
        using var cts = new CancellationTokenSource();
        var reader = Task.Run(async () =>
        {
            await foreach (var e in hub.SubscribeAsync(cts.Token))
            {
                received.Add(e);
                if (e is TurnEvent { Edge: TurnEdge.BotStopped }) cts.Cancel();
            }
        });
        while (!hub.HasListeners) await Task.Delay(5);

        hub.Publish(new TurnEvent(TurnEdge.UserStopped));
        hub.Publish(new TurnEvent(TurnEdge.Interrupted));          // barge-in before the transcript
        hub.Publish(new TranscriptEvent("orphan", IsFinal: true)); // must NOT derive stt_final
        hub.Publish(new TurnEvent(TurnEdge.BotStopped));

        await Awaited(reader);
        Assert.DoesNotContain(received.OfType<StageLatencyEvent>(), s => s.Stage == "stt_final");
    }

    // ── Tap → event mapping per scope ────────────────────────────────────────

    [Fact]
    public async Task Taps_Publish_Only_What_Their_Position_Owns_And_Always_Forward()
    {
        var hub = new VoxaDiagnosticsHub();
        var received = new List<DiagnosticEvent>();
        using var cts = new CancellationTokenSource();
        var reader = Task.Run(async () =>
        {
            await foreach (var e in hub.SubscribeAsync(cts.Token))
                received.Add(e);
        });
        while (!hub.HasListeners) await Task.Delay(5);

        // Chain all four taps — exactly how the composer stacks them — and push one frame of
        // each interesting kind through the whole chain.
        var vad = new DiagnosticsTapProcessor(hub, DiagnosticsTapScope.Vad);
        var stt = new DiagnosticsTapProcessor(hub, DiagnosticsTapScope.Stt);
        var agent = new DiagnosticsTapProcessor(hub, DiagnosticsTapScope.Agent);
        var tts = new DiagnosticsTapProcessor(hub, DiagnosticsTapScope.Tts);
        var collector = new CollectorProcessor();
        vad.Link(stt); stt.Link(agent); agent.Link(tts); tts.Link(collector);
        foreach (var p in new FrameProcessor[] { vad, stt, agent, tts, collector }) p.Start();

        var frames = new Frame[]
        {
            new UserStartedSpeakingFrame(),
            new UserStoppedSpeakingFrame(),
            new TranscriptionFrame("hi there", IsFinal: true),
            new LlmTextChunkFrame("You said: hi there."),
            new BotStartedSpeakingFrame(),
            new AudioRawFrame(new byte[640], 16000, 1),
            new BotStoppedSpeakingFrame(),
        };
        foreach (var f in frames) await vad.QueueFrameAsync(f);
        await collector.WaitForAsync(frames.Length, TimeSpan.FromSeconds(5));
        cts.Cancel();
        await Awaited(reader);
        foreach (var p in new FrameProcessor[] { vad, stt, agent, tts, collector }) await p.DisposeAsync();

        // Every frame reached the end of the chain untouched.
        Assert.Equal(frames.Length, collector.Frames.Count);

        // ...and each frame surfaced as exactly ONE event, from the tap that owns it.
        Assert.Equal(2, received.Count(e => e is TurnEvent { Edge: TurnEdge.UserStarted or TurnEdge.UserStopped }));
        Assert.Single(received.OfType<TranscriptEvent>());
        Assert.Single(received.OfType<AgentDeltaEvent>());
        Assert.Single(received.OfType<TtsChunkEvent>());
        Assert.Equal(2, received.Count(e => e is TurnEvent { Edge: TurnEdge.BotStarted or TurnEdge.BotStopped }));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private sealed class CollectorProcessor : FrameProcessor
    {
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _expected = int.MaxValue;

        public List<Frame> Frames { get; } = new();

        public CollectorProcessor() : base("Collector") { }

        protected override ValueTask ProcessFrameAsync(Frame frame, CancellationToken ct)
        {
            lock (Frames)
            {
                Frames.Add(frame);
                if (Frames.Count >= _expected) _done.TrySetResult();
            }
            return ValueTask.CompletedTask;
        }

        public Task WaitForAsync(int count, TimeSpan timeout)
        {
            lock (Frames)
            {
                _expected = count;
                if (Frames.Count >= count) return Task.CompletedTask;
            }
            return _done.Task.WaitAsync(timeout);
        }
    }

    /// <summary>Await a reader task that ends via cancellation without surfacing the OCE.</summary>
    private static async Task Awaited(Task t)
    {
        try { await t.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (OperationCanceledException) { }
    }
}
