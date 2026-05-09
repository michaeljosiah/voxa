using Voxa.Frames;
using Voxa.Pipelines;
using Voxa.Processors;
using Voxa.Speech;
using Voxa.Testing.Processors;

namespace Voxa.Speech.Abstractions.Tests;

public class TextToSpeechProcessorTests
{
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
    public async Task TextFrame_Triggers_Synthesis_With_Speaking_Bookends()
    {
        var (runner, engine, captured, pipeline) = Build();
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(40);

            await pipeline.Source.IngestAsync(new TextFrame("hello"));
            await Task.Delay(150);

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
            await Task.Delay(150);

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
            await Task.Delay(150);

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
            await Task.Delay(200);

            var audioFrames = captured.Captured.OfType<AudioRawFrame>().ToList();
            Assert.Equal(3, audioFrames.Count);
        }
    }

    [Fact]
    public async Task Empty_TextFrame_Is_Ignored()
    {
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
        // Critical for transports/UI: the TextFrame must reach the sink so
        // it can be serialized as a `text` envelope (e.g. to render in a chat
        // bubble) BEFORE the audio chunks arrive.
        var (runner, _, captured, pipeline) = Build();
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(40);

            await pipeline.Source.IngestAsync(new TextFrame("hi there"));
            await Task.Delay(150);

            var indexOfText = IndexOf(captured.Captured, f => f is TextFrame t && t.Text == "hi there");
            var indexOfStarted = IndexOf(captured.Captured, f => f is BotStartedSpeakingFrame);
            var indexOfAudio = IndexOf(captured.Captured, f => f is AudioRawFrame);

            Assert.True(indexOfText >= 0, "TextFrame must be forwarded downstream so the sink/UI can render it.");
            Assert.True(indexOfStarted >= 0);
            Assert.True(indexOfAudio >= 0);
            Assert.True(indexOfText < indexOfStarted, "TextFrame must arrive BEFORE BotStartedSpeakingFrame.");
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
            await Task.Delay(150);

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
