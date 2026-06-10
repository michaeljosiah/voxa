using System.Buffers.Binary;
using Voxa.Audio.SileroVad;
using Voxa.Frames;
using Voxa.Pipelines;
using Voxa.Processors;
using Voxa.Testing.Processors;

namespace Voxa.Audio.SileroVad.Tests;

public class SileroVadProcessorTests
{
    private static (PipelineRunner Runner, CapturingProcessor Captured, Pipeline Pipeline) Build(
        SileroVadOptions? options = null)
    {
        var captured = new CapturingProcessor();
        var pipeline = Pipeline.Build()
            .Source(new PipelineSource())
            .Then(new SileroVadProcessor(options))
            .Then(captured)
            .Sink(new PipelineSink());
        return (new PipelineRunner(pipeline), captured, pipeline);
    }

    private static byte[] Silence(int samples)
    {
        return new byte[samples * 2];
    }

    private static byte[] WhiteNoise(int samples, int seed = 42, double amplitude = 0.5)
    {
        var rng = new Random(seed);
        var bytes = new byte[samples * 2];
        for (int i = 0; i < samples; i++)
        {
            short s = (short)((rng.NextDouble() * 2 - 1) * amplitude * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), s);
        }
        return bytes;
    }

    [Fact]
    public async Task Pure_Silence_Does_Not_Open_The_Gate()
    {
        var (runner, captured, pipeline) = Build();
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(40);

            // 16 windows of silence — well past MinSpeechWindows.
            var pcm = Silence(512 * 16);
            await pipeline.Source.IngestAsync(new AudioRawFrame(pcm, 16000, 1));
            await Task.Delay(150);

            Assert.DoesNotContain(captured.Captured, f => f is UserStartedSpeakingFrame);
            Assert.DoesNotContain(captured.Captured, f => f is AudioRawFrame);
        }
    }

    [Fact]
    public async Task Mismatched_Sample_Rate_Frames_Are_Forwarded_Untouched()
    {
        var (runner, captured, pipeline) = Build();
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(40);

            // Configured for 16 kHz but we send 24 kHz — should pass through.
            // Wait on the frame rather than a fixed delay: on a cold CI runner the first
            // inference pays ONNX runtime init + model load, which can exceed any fixed budget.
            var pcm = WhiteNoise(1024);
            await pipeline.Source.IngestAsync(new AudioRawFrame(pcm, 24000, 1));
            await captured.WaitForAsync(
                f => f is AudioRawFrame a && a.SampleRate == 24000, TimeSpan.FromSeconds(10));

            Assert.Contains(captured.Captured, f => f is AudioRawFrame a && a.SampleRate == 24000);
        }
    }

    [Fact]
    public async Task Non_Audio_Frames_Always_Forward()
    {
        var (runner, captured, pipeline) = Build();
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(40);

            await pipeline.Source.IngestAsync(new TextFrame("hello"));
            await pipeline.Source.IngestAsync(new TranscriptionFrame("hi", IsFinal: true));
            await captured.WaitForAsync(3, TimeSpan.FromSeconds(2));

            Assert.Contains(captured.Captured, f => f is TextFrame);
            Assert.Contains(captured.Captured, f => f is TranscriptionFrame);
        }
    }

    [Fact]
    public async Task Non_16k_Configuration_Still_Loads()
    {
        var opts = new SileroVadOptions { SampleRate = 8000 };
        var (runner, _, pipeline) = Build(opts);
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(40);
            // 256-sample windows for 8k.
            await pipeline.Source.IngestAsync(new AudioRawFrame(Silence(256 * 4), 8000, 1));
            await Task.Delay(80);
            // No exception = passing.
        }
    }
}
