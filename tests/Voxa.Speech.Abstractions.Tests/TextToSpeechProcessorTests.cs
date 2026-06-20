using Voxa.Frames;
using Voxa.Pipelines;
using Voxa.Processors;
using Voxa.Speech;
using Voxa.Testing.Processors;

namespace Voxa.Speech.Abstractions.Tests;

public class TextToSpeechProcessorTests
{
    // Deflaked (the documented "ingest → fixed Task.Delay → assert" anti-pattern): positive
    // tests poll for the frames they assert on instead of sleeping a fixed 150 ms that slow CI
    // runners blow through.
    //
    // Ordering caveat that made the original test flaky beyond timing: speaking events
    // (BotStarted/StoppedSpeakingFrame) are SYSTEM frames — they travel each processor's
    // priority channel, concurrent with the data channel. Arrival order BETWEEN the two
    // channels is architecturally unguaranteed (system frames are supposed to jump ahead).
    // The orderings these tests may assert are within the DATA channel only (FIFO):
    // TextFrame/LlmTextChunkFrame before the first AudioRawFrame — the wire contract that the
    // chat-bubble text envelope precedes audio bytes. Waits therefore target the data frames
    // themselves, never a system-frame bookend that could overtake them.
    private static readonly TimeSpan WaitCap = TimeSpan.FromSeconds(10);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + WaitCap;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }
    }

    private static (PipelineRunner Runner, ScriptedTextToSpeechEngine Engine, CapturingProcessor Captured, Pipeline Pipeline) Build(
        Func<string, byte[][]>? generate = null,
        int outputSampleRate = 24000)
    {
        var engine = new ScriptedTextToSpeechEngine(generate);
        var processor = new TextToSpeechProcessor(() => engine, outputSampleRate);
        var captured = new CapturingProcessor();
        var pipeline = Pipeline.Build()
            .Source(new PipelineSource())
            .Then(processor)
            .Then(captured)
            .Sink(new PipelineSink());
        return (new PipelineRunner(pipeline), engine, captured, pipeline);
    }

    [Fact]
    public async Task Disposing_Without_An_EndFrame_Disposes_The_Engine()
    {
        // CQ-003: an abrupt teardown (client disconnect, no EndFrame) must still dispose the TTS engine via
        // DisposeAsyncCore — not only OnEndAsync.
        var (runner, engine, _, _) = Build();
        await runner.StartAsync();
        await Task.Delay(60); // OnStartAsync created + started the engine

        await runner.DisposeAsync(); // abrupt: no EndFrame is ever injected

        Assert.True(engine.Disposed);
    }

    [Fact]
    public async Task TextFrame_Triggers_Synthesis_With_Speaking_Bookends()
    {
        var (runner, engine, captured, pipeline) = Build();
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(40);

            await pipeline.Source.IngestAsync(new TextFrame("hello"));
            await WaitUntilAsync(() =>
                captured.Captured.OfType<AudioRawFrame>().Any() &&
                captured.Captured.OfType<BotStoppedSpeakingFrame>().Any());

            Assert.Single(engine.SynthesizeCalls);
            Assert.Equal("hello", engine.SynthesizeCalls[0]);
            Assert.Contains(captured.Captured, f => f is BotStartedSpeakingFrame);
            Assert.Contains(captured.Captured, f => f is AudioRawFrame);
            Assert.Contains(captured.Captured, f => f is BotStoppedSpeakingFrame);
        }
    }

    [Fact]
    public async Task LlmTextChunkFrame_Also_Triggers_Synthesis()
    {
        var (runner, engine, captured, pipeline) = Build();
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(40);

            await pipeline.Source.IngestAsync(new LlmTextChunkFrame("from llm"));
            await WaitUntilAsync(() => captured.Captured.OfType<AudioRawFrame>().Any());

            Assert.Equal(new[] { "from llm" }, engine.SynthesizeCalls);
            Assert.Contains(captured.Captured, f => f is AudioRawFrame);
        }
    }

    [Fact]
    public async Task AudioRawFrame_Carries_Configured_Sample_Rate()
    {
        var (runner, _, captured, pipeline) = Build(
            generate: text => new[] { new byte[] { 1, 2, 3, 4 } },
            outputSampleRate: 16000);
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(40);
            await pipeline.Source.IngestAsync(new TextFrame("x"));
            await WaitUntilAsync(() => captured.Captured.OfType<AudioRawFrame>().Any());

            var audio = captured.Captured.OfType<AudioRawFrame>().FirstOrDefault();
            Assert.NotNull(audio);
            Assert.Equal(16000, audio!.SampleRate);
            Assert.Equal(1, audio.Channels);
        }
    }

    [Fact]
    public async Task Multi_Chunk_Synthesis_Emits_Multiple_AudioRawFrames()
    {
        var (runner, _, captured, pipeline) = Build(
            generate: text => new[]
            {
                new byte[] { 0xAA },
                new byte[] { 0xBB },
                new byte[] { 0xCC },
            });
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(40);
            await pipeline.Source.IngestAsync(new TextFrame("triple"));
            await WaitUntilAsync(() => captured.Captured.OfType<AudioRawFrame>().Count() >= 3);

            var audioFrames = captured.Captured.OfType<AudioRawFrame>().ToList();
            Assert.Equal(3, audioFrames.Count);
        }
    }

    [Fact]
    public async Task Empty_TextFrame_Is_Ignored()
    {
        // Negative assertion ("nothing happened") — polling can't prove absence, so this one
        // keeps a fixed observation window by design.
        var (runner, engine, _, pipeline) = Build();
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(40);
            await pipeline.Source.IngestAsync(new TextFrame("   "));
            await Task.Delay(80);
            Assert.Empty(engine.SynthesizeCalls);
        }
    }

    [Fact]
    public async Task TextFrame_Is_Forwarded_Downstream_Before_Audio()
    {
        // Critical for transports/UI: the TextFrame must reach the sink so it can be serialized
        // as a `text` envelope (e.g. to render in a chat bubble) BEFORE the audio chunks arrive.
        // Both are data frames, so this order is a real FIFO guarantee. No ordering is asserted
        // against BotStartedSpeakingFrame: it is a system frame on the priority channel and may
        // legitimately overtake the text (the historical flake in this test was exactly that).
        var (runner, _, captured, pipeline) = Build();
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(40);

            await pipeline.Source.IngestAsync(new TextFrame("hi there"));
            await WaitUntilAsync(() =>
                captured.Captured.OfType<AudioRawFrame>().Any() &&
                captured.Captured.OfType<BotStartedSpeakingFrame>().Any());

            var indexOfText = IndexOf(captured.Captured, f => f is TextFrame t && t.Text == "hi there");
            var indexOfAudio = IndexOf(captured.Captured, f => f is AudioRawFrame);

            Assert.True(indexOfText >= 0, "TextFrame must be forwarded downstream so the sink/UI can render it.");
            Assert.Contains(captured.Captured, f => f is BotStartedSpeakingFrame);
            Assert.True(indexOfAudio >= 0);
            Assert.True(indexOfText < indexOfAudio, "TextFrame must arrive BEFORE the first AudioRawFrame.");
        }
    }

    [Fact]
    public async Task LlmTextChunkFrame_Is_Forwarded_Downstream_Before_Audio()
    {
        var (runner, _, captured, pipeline) = Build();
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(40);

            await pipeline.Source.IngestAsync(new LlmTextChunkFrame("chunk one"));
            await WaitUntilAsync(() => captured.Captured.OfType<AudioRawFrame>().Any());

            var indexOfChunk = IndexOf(captured.Captured, f => f is LlmTextChunkFrame c && c.Text == "chunk one");
            var indexOfAudio = IndexOf(captured.Captured, f => f is AudioRawFrame);

            Assert.True(indexOfChunk >= 0, "LlmTextChunkFrame must be forwarded downstream.");
            Assert.True(indexOfAudio >= 0);
            Assert.True(indexOfChunk < indexOfAudio, "LlmTextChunkFrame must arrive BEFORE the first AudioRawFrame.");
        }
    }

    private static int IndexOf(IReadOnlyList<Frame> source, Func<Frame, bool> predicate)
    {
        for (int i = 0; i < source.Count; i++)
        {
            if (predicate(source[i])) return i;
        }
        return -1;
    }
}
