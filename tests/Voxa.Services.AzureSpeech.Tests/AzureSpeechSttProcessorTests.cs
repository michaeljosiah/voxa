using Voxa.Frames;
using Voxa.Pipelines;
using Voxa.Processors;
using Voxa.Testing.Processors;

namespace Voxa.Services.AzureSpeech.Tests;

public class AzureSpeechSttProcessorTests
{
    private static (PipelineRunner Runner, ScriptedSpeechToTextEngine Engine, CapturingProcessor Captured, Pipeline Pipeline)
        Build()
    {
        var engine = new ScriptedSpeechToTextEngine();
        var processor = new AzureSpeechSttProcessor(() => engine);
        var captured = new CapturingProcessor();
        var pipeline = Pipeline.Build()
            .Source(new PipelineSource())
            .Then(processor)
            .Then(captured)
            .Sink(new PipelineSink());
        return (new PipelineRunner(pipeline), engine, captured, pipeline);
    }

    [Fact]
    public async Task Engine_Is_Started_On_StartFrame()
    {
        var (runner, engine, _, _) = Build();
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(60);
            Assert.True(engine.Started);
        }
    }

    [Fact]
    public async Task AudioRawFrame_Is_Written_To_Engine()
    {
        var (runner, engine, _, pipeline) = Build();
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(40);

            var pcm = new byte[] { 1, 2, 3, 4, 5, 6 };
            await pipeline.Source.IngestAsync(new AudioRawFrame(pcm, 16000, 1));
            await Task.Delay(60);

            Assert.Single(engine.WrittenAudio);
            Assert.Equal(pcm, engine.WrittenAudio[0]);
        }
    }

    [Fact]
    public async Task Engine_Transcripts_Become_TranscriptionFrames()
    {
        var (runner, engine, captured, _) = Build();
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(40);

            await engine.QueueTranscriptAsync("hello", isFinal: false);
            await engine.QueueTranscriptAsync("hello world", isFinal: true);

            await captured.WaitForAsync(3, TimeSpan.FromSeconds(2));
            var transcripts = captured.Captured.OfType<TranscriptionFrame>().ToList();
            Assert.Equal(2, transcripts.Count);
            Assert.False(transcripts[0].IsFinal);
            Assert.Equal("hello", transcripts[0].Text);
            Assert.True(transcripts[1].IsFinal);
            Assert.Equal("hello world", transcripts[1].Text);
        }
    }

    [Fact]
    public async Task Engine_Is_Disposed_On_EndFrame()
    {
        var (runner, engine, _, _) = Build();
        await using (runner)
        {
            await runner.StartAsync();
            await runner.StopAsync(TimeSpan.FromSeconds(2));
            await runner.WaitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(40);
            Assert.True(engine.Disposed);
        }
    }
}
