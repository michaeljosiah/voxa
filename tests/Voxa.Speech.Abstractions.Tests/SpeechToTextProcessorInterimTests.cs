using System.Threading.Channels;
using Voxa.Frames;
using Voxa.Pipelines;
using Voxa.Processors;
using Voxa.Speech;
using Voxa.Testing.Processors;

namespace Voxa.Speech.Abstractions.Tests;

/// <summary>
/// VRT-004 WS1: interim transcripts already flow; the processor only COALESCES them (≤ one per
/// <c>InterimMinInterval</c>) so a chatty streaming engine can't flood the bounded data channel. Finals are
/// never coalesced, empties are dropped, and interims keep flowing when spaced beyond the interval (preserved,
/// not gated).
/// </summary>
public class SpeechToTextProcessorInterimTests
{
    /// <summary>A streaming engine whose transcripts the test pushes on demand (interim or final).</summary>
    private sealed class ScriptedSttEngine : ISpeechToTextEngine
    {
        private readonly Channel<TranscriptionResult> _ch =
            Channel.CreateUnbounded<TranscriptionResult>(new UnboundedChannelOptions { SingleReader = true });

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask WriteAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct) => ValueTask.CompletedTask;
        public IAsyncEnumerable<TranscriptionResult> ReadTranscriptsAsync(CancellationToken ct) => _ch.Reader.ReadAllAsync(ct);
        public Task StopAsync() { _ch.Writer.TryComplete(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ValueTask EmitAsync(string text, bool isFinal)
            => _ch.Writer.WriteAsync(new TranscriptionResult(text, isFinal));
    }

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    private static (PipelineRunner Runner, CapturingProcessor Cap, Pipeline Pipeline) Build(
        ScriptedSttEngine engine, TimeSpan interimMinInterval)
    {
        var stt = new SpeechToTextProcessor(engine) { InterimMinInterval = interimMinInterval };
        var cap = new CapturingProcessor();
        var pipeline = Pipeline.Build()
            .Source(new PipelineSource())
            .Then(stt)
            .Then(cap)
            .Sink(new PipelineSink());
        return (new PipelineRunner(pipeline), cap, pipeline);
    }

    private static List<TranscriptionFrame> Transcripts(CapturingProcessor cap)
        => cap.Captured.OfType<TranscriptionFrame>().ToList();

    [Fact]
    public async Task Rapid_Interims_Are_Coalesced_To_One_Per_Window_And_The_Final_Always_Flows()
    {
        var engine = new ScriptedSttEngine();
        var (runner, cap, _) = Build(engine, TimeSpan.FromSeconds(10)); // huge window ⇒ all but the first coalesced

        await using (runner)
        {
            await runner.StartAsync();
            await engine.EmitAsync("what", isFinal: false);
            await engine.EmitAsync("what's", isFinal: false);
            await engine.EmitAsync("what's the", isFinal: false);
            await engine.EmitAsync("what's the weather", isFinal: true);

            await cap.WaitForAsync(f => f is TranscriptionFrame { IsFinal: true }, Timeout);
            await Task.Delay(40);

            var transcripts = Transcripts(cap);
            Assert.Single(transcripts, t => !t.IsFinal);                             // exactly one interim survived
            Assert.Contains(transcripts, t => t.IsFinal && t.Text == "what's the weather"); // final always flows
        }
    }

    [Fact]
    public async Task Finals_Are_Never_Coalesced()
    {
        var engine = new ScriptedSttEngine();
        var (runner, cap, _) = Build(engine, TimeSpan.FromSeconds(10)); // would coalesce interims, but not finals

        await using (runner)
        {
            await runner.StartAsync();
            await engine.EmitAsync("one", isFinal: true);
            await engine.EmitAsync("two", isFinal: true);
            await engine.EmitAsync("three", isFinal: true);

            await cap.WaitForAsync(f => f is TranscriptionFrame { IsFinal: true, Text: "three" }, Timeout);
            await Task.Delay(40);

            var finals = Transcripts(cap).Where(t => t.IsFinal).Select(t => t.Text).ToList();
            Assert.Equal(new[] { "one", "two", "three" }, finals); // every final forwarded, in order
        }
    }

    [Fact]
    public async Task Empty_Interims_Are_Dropped()
    {
        var engine = new ScriptedSttEngine();
        var (runner, cap, _) = Build(engine, TimeSpan.FromMilliseconds(1));

        await using (runner)
        {
            await runner.StartAsync();
            await engine.EmitAsync("   ", isFinal: false); // whitespace interim → dropped
            await engine.EmitAsync("real", isFinal: true);

            await cap.WaitForAsync(f => f is TranscriptionFrame { IsFinal: true }, Timeout);
            await Task.Delay(40);

            Assert.DoesNotContain(Transcripts(cap), t => !t.IsFinal); // no empty interim leaked
        }
    }

    [Fact]
    public async Task Interims_Spaced_Beyond_The_Interval_All_Flow_Preserved_Not_Gated()
    {
        var engine = new ScriptedSttEngine();
        var (runner, cap, _) = Build(engine, TimeSpan.FromMilliseconds(1)); // tiny window ⇒ spaced interims pass

        await using (runner)
        {
            await runner.StartAsync();
            await engine.EmitAsync("a", isFinal: false);
            await Task.Delay(25);
            await engine.EmitAsync("ab", isFinal: false);
            await Task.Delay(25);
            await engine.EmitAsync("abc", isFinal: true);

            await cap.WaitForAsync(f => f is TranscriptionFrame { IsFinal: true }, Timeout);
            await Task.Delay(40);

            // Both spaced interims flowed — coalescing rate-limits, it does not suppress.
            Assert.True(Transcripts(cap).Count(t => !t.IsFinal) >= 2);
        }
    }
}
