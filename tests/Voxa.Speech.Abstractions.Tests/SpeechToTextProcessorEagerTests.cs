using System.Threading.Channels;
using Voxa.Frames;
using Voxa.Pipelines;
using Voxa.Processors;
using Voxa.Speech;
using Voxa.Testing.Processors;

namespace Voxa.Speech.Abstractions.Tests;

/// <summary>
/// VRT-002 WS1: the STT processor coordinates eager/speculative flushes. A superseded speculative final is
/// dropped before it becomes a <see cref="TranscriptionFrame"/> (the suppression guarantee); a confirmed turn
/// promotes the speculative pass by discarding the buffer instead of re-flushing (no double STT pass).
/// </summary>
public class SpeechToTextProcessorEagerTests
{
    /// <summary>Records every engine call and lets the test push transcripts on demand.</summary>
    private sealed class FakeSttEngine : ISpeechToTextEngine
    {
        private readonly Channel<TranscriptionResult> _ch =
            Channel.CreateUnbounded<TranscriptionResult>(new UnboundedChannelOptions { SingleReader = true });
        private readonly object _lock = new();
        private readonly List<string> _calls = new();
        private readonly bool _supportsEager;

        public FakeSttEngine(bool supportsEager = true) => _supportsEager = supportsEager;

        public bool SupportsEagerSttFlush => _supportsEager;

        public Task StartAsync(CancellationToken ct) { Record("start"); return Task.CompletedTask; }
        public ValueTask WriteAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct) { Record($"write:{pcm.Length}"); return ValueTask.CompletedTask; }
        public IAsyncEnumerable<TranscriptionResult> ReadTranscriptsAsync(CancellationToken ct) => _ch.Reader.ReadAllAsync(ct);
        public Task StopAsync() { Record("stop"); _ch.Writer.TryComplete(); return Task.CompletedTask; }
        public Task FlushAsync() { Record("flush"); return Task.CompletedTask; }
        public Task FlushAsync(long utteranceId) { Record($"flush:{utteranceId}"); return Task.CompletedTask; }
        public Task DiscardBufferedAudioAsync() { Record("discard"); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Record("dispose"); return ValueTask.CompletedTask; }

        public ValueTask EmitFinalAsync(string text, long? utteranceId)
            => _ch.Writer.WriteAsync(new TranscriptionResult(text, IsFinal: true, UtteranceId: utteranceId));

        private void Record(string s) { lock (_lock) _calls.Add(s); }
        public IReadOnlyList<string> Calls { get { lock (_lock) return _calls.ToList(); } }
    }

    private static (PipelineRunner Runner, CapturingProcessor Cap, Pipeline Pipeline) Build(FakeSttEngine engine)
    {
        var stt = new SpeechToTextProcessor(engine);
        var cap = new CapturingProcessor();
        var pipeline = Pipeline.Build()
            .Source(new PipelineSource())
            .Then(stt)
            .Then(cap)
            .Sink(new PipelineSink());
        return (new PipelineRunner(pipeline), cap, pipeline);
    }

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
    }

    [Fact]
    public async Task Speculative_Frame_Triggers_An_Id_Tagged_Flush()
    {
        var engine = new FakeSttEngine();
        var (runner, _, pipeline) = Build(engine);

        await using (runner)
        {
            await runner.StartAsync();
            await WaitUntilAsync(() => engine.Calls.Contains("start"), Timeout); // engine ready before frames
            await pipeline.Source.IngestAsync(new SpeculativeUtteranceFrame(7));

            await WaitUntilAsync(() => engine.Calls.Contains("flush:7"), Timeout);
            Assert.Contains("flush:7", engine.Calls);
        }
    }

    [Fact]
    public async Task Superseded_Speculative_Final_Is_Dropped_Before_Becoming_A_TranscriptionFrame()
    {
        var engine = new FakeSttEngine();
        var (runner, cap, pipeline) = Build(engine);

        await using (runner)
        {
            await runner.StartAsync();
            await WaitUntilAsync(() => engine.Calls.Contains("start"), Timeout); // engine ready before frames
            await pipeline.Source.IngestAsync(new SpeculativeUtteranceFrame(5));               // arm
            await pipeline.Source.IngestAsync(new SpeculativeUtteranceFrame(5, Superseded: true)); // supersede
            await WaitUntilAsync(() => engine.Calls.Contains("flush:5"), Timeout);
            await Task.Delay(50); // let the supersede be recorded before the finals arrive

            await engine.EmitFinalAsync("stale speculative", utteranceId: 5);  // must be suppressed
            await engine.EmitFinalAsync("the real turn", utteranceId: null);   // must pass through

            await cap.WaitForAsync(
                f => f is TranscriptionFrame t && t.Text == "the real turn", Timeout);

            var texts = cap.Captured.OfType<TranscriptionFrame>().Select(t => t.Text).ToList();
            Assert.Contains("the real turn", texts);
            Assert.DoesNotContain("stale speculative", texts); // suppression guarantee
        }
    }

    [Fact]
    public async Task Speculative_Final_Is_Held_Until_Confirmed_Then_Forwarded()
    {
        var engine = new FakeSttEngine();
        var (runner, cap, pipeline) = Build(engine);

        await using (runner)
        {
            await runner.StartAsync();
            await WaitUntilAsync(() => engine.Calls.Contains("start"), Timeout);
            await pipeline.Source.IngestAsync(new SpeculativeUtteranceFrame(9)); // arm
            await WaitUntilAsync(() => engine.Calls.Contains("flush:9"), Timeout);

            await engine.EmitFinalAsync("held text", utteranceId: 9);             // arrives before confirm
            await Task.Delay(80);
            // Held, not forwarded yet: a fast engine's final must not start a turn before the VAD confirms.
            Assert.DoesNotContain(cap.Captured.OfType<TranscriptionFrame>(), t => t.Text == "held text");

            await pipeline.Source.IngestAsync(new UserStoppedSpeakingFrame());    // confirm ⇒ promote
            await cap.WaitForAsync(f => f is TranscriptionFrame t && t.Text == "held text", Timeout);
            Assert.Contains(cap.Captured.OfType<TranscriptionFrame>(), t => t.Text == "held text");
        }
    }

    [Fact]
    public async Task Speculative_Final_Arriving_After_Confirm_Is_Forwarded()
    {
        var engine = new FakeSttEngine();
        var (runner, cap, pipeline) = Build(engine);

        await using (runner)
        {
            await runner.StartAsync();
            await WaitUntilAsync(() => engine.Calls.Contains("start"), Timeout);
            await pipeline.Source.IngestAsync(new SpeculativeUtteranceFrame(9));  // arm
            await WaitUntilAsync(() => engine.Calls.Contains("flush:9"), Timeout);
            await pipeline.Source.IngestAsync(new UserStoppedSpeakingFrame());    // confirm before the final
            await WaitUntilAsync(() => engine.Calls.Contains("discard"), Timeout);

            await engine.EmitFinalAsync("late text", utteranceId: 9);             // arrives after confirm
            await cap.WaitForAsync(f => f is TranscriptionFrame t && t.Text == "late text", Timeout);
            Assert.Contains(cap.Captured.OfType<TranscriptionFrame>(), t => t.Text == "late text");
        }
    }

    [Fact]
    public async Task Speculative_Final_Arriving_Before_Supersede_Is_Still_Dropped()
    {
        // The race Codex flagged: a fast engine returns the tagged final BEFORE the VAD's supersede frame.
        // Holding the final until the verdict means the later supersede still drops it.
        var engine = new FakeSttEngine();
        var (runner, cap, pipeline) = Build(engine);

        await using (runner)
        {
            await runner.StartAsync();
            await WaitUntilAsync(() => engine.Calls.Contains("start"), Timeout);
            await pipeline.Source.IngestAsync(new SpeculativeUtteranceFrame(5));            // arm
            await WaitUntilAsync(() => engine.Calls.Contains("flush:5"), Timeout);

            await engine.EmitFinalAsync("stale speculative", utteranceId: 5);               // final FIRST (held)
            await Task.Delay(80);
            await pipeline.Source.IngestAsync(new SpeculativeUtteranceFrame(5, Superseded: true)); // then supersede
            await Task.Delay(80);

            // A real later turn proves the pipeline still flows — and the stale held final never shipped.
            await engine.EmitFinalAsync("the real turn", utteranceId: null);
            await cap.WaitForAsync(f => f is TranscriptionFrame t && t.Text == "the real turn", Timeout);

            var texts = cap.Captured.OfType<TranscriptionFrame>().Select(t => t.Text).ToList();
            Assert.DoesNotContain("stale speculative", texts);
            Assert.Contains("the real turn", texts);
        }
    }

    [Fact]
    public async Task Confirm_After_Speculative_Discards_Buffer_Instead_Of_Reflushing()
    {
        var engine = new FakeSttEngine();
        var (runner, _, pipeline) = Build(engine);

        await using (runner)
        {
            await runner.StartAsync();
            await WaitUntilAsync(() => engine.Calls.Contains("start"), Timeout); // engine ready before frames
            await pipeline.Source.IngestAsync(new SpeculativeUtteranceFrame(3));       // pending speculative
            await WaitUntilAsync(() => engine.Calls.Contains("flush:3"), Timeout);
            await pipeline.Source.IngestAsync(new UserStoppedSpeakingFrame());          // confirm ⇒ promote

            await WaitUntilAsync(() => engine.Calls.Contains("discard"), Timeout);
            // Promote drops the buffer; it must NOT issue a second (plain) flush for this turn.
            Assert.Contains("discard", engine.Calls);
            Assert.DoesNotContain("flush", engine.Calls); // bare parameterless flush never happened
        }
    }

    [Fact]
    public async Task NonEager_Engine_Ignores_Speculative_And_Flushes_At_Speech_End()
    {
        // An engine that doesn't advertise SupportsEagerSttFlush must never see a speculative flush (which
        // would emit an untagged final + clear its buffer). The marker is ignored; the turn flushes normally.
        var engine = new FakeSttEngine(supportsEager: false);
        var (runner, cap, pipeline) = Build(engine);

        await using (runner)
        {
            await runner.StartAsync();
            await WaitUntilAsync(() => engine.Calls.Contains("start"), Timeout);
            await pipeline.Source.IngestAsync(new SpeculativeUtteranceFrame(7));
            await Task.Delay(80);
            Assert.DoesNotContain("flush:7", engine.Calls); // no speculative flush issued

            await pipeline.Source.IngestAsync(new UserStoppedSpeakingFrame());
            await WaitUntilAsync(() => engine.Calls.Contains("flush"), Timeout);
            Assert.Contains("flush", engine.Calls);          // classic flush at speech-end
            Assert.DoesNotContain("discard", engine.Calls);  // nothing speculative to promote
        }
    }

    [Fact]
    public async Task UserStopped_Without_Speculative_Flushes_Classically()
    {
        var engine = new FakeSttEngine();
        var (runner, _, pipeline) = Build(engine);

        await using (runner)
        {
            await runner.StartAsync();
            await WaitUntilAsync(() => engine.Calls.Contains("start"), Timeout); // engine ready before frames
            await pipeline.Source.IngestAsync(new UserStoppedSpeakingFrame()); // no eager pass pending

            await WaitUntilAsync(() => engine.Calls.Contains("flush"), Timeout);
            Assert.Contains("flush", engine.Calls);          // classic batch flush
            Assert.DoesNotContain("discard", engine.Calls);  // nothing to promote
        }
    }
}
