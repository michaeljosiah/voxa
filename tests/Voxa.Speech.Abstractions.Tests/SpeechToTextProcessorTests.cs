using Voxa.Frames;
using Voxa.Pipelines;
using Voxa.Processors;
using Voxa.Speech;
using Voxa.Testing.Processors;

namespace Voxa.Speech.Abstractions.Tests;

public class SpeechToTextProcessorTests
{
    private static (PipelineRunner Runner, ScriptedSpeechToTextEngine Engine, CapturingProcessor Captured, Pipeline Pipeline) Build()
    {
        var engine = new ScriptedSpeechToTextEngine();
        var processor = new SpeechToTextProcessor(() => engine);
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
    public async Task AudioRawFrame_Is_Written_To_Engine_And_Not_Forwarded()
    {
        var (runner, engine, captured, pipeline) = Build();
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(40);

            var pcm = new byte[] { 1, 2, 3, 4 };
            await pipeline.Source.IngestAsync(new AudioRawFrame(pcm, 16000, 1));
            await Task.Delay(60);

            Assert.Single(engine.WrittenAudio);
            Assert.Equal(pcm, engine.WrittenAudio[0]);
            Assert.DoesNotContain(captured.Captured, f => f is AudioRawFrame);
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
            Assert.True(transcripts[1].IsFinal);
        }
    }

    [Fact]
    public async Task Non_Audio_Frames_Are_Forwarded()
    {
        var (runner, _, captured, pipeline) = Build();
        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new TextFrame("system message"));
            await captured.WaitForAsync(2, TimeSpan.FromSeconds(2));

            Assert.Contains(captured.Captured, f => f is TextFrame);
        }
    }

    [Fact]
    public async Task UserStoppedSpeakingFrame_Triggers_Engine_FlushAsync()
    {
        var (runner, engine, _, pipeline) = Build();
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(40);

            Assert.Equal(0, engine.FlushCount);
            await pipeline.Source.IngestAsync(new UserStoppedSpeakingFrame());
            await Task.Delay(60);

            Assert.Equal(1, engine.FlushCount);
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
