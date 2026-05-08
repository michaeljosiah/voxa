using Voxa.Frames;
using Voxa.Pipelines;
using Voxa.Processors;
using Voxa.Testing.Processors;

namespace Voxa.Services.AzureSpeech.Tests;

public class AzureSpeechTtsProcessorTests
{
    private static (PipelineRunner Runner, ScriptedTextToSpeechEngine Engine, CapturingProcessor Captured, Pipeline Pipeline)
        Build(Func<string, byte[][]>? generate = null, int outputSampleRate = 24000)
    {
        var engine = new ScriptedTextToSpeechEngine(generate);
        var processor = new AzureSpeechTtsProcessor(() => engine, outputSampleRate);
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
}
