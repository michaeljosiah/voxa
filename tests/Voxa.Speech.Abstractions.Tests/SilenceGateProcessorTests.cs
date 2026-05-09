using System.Buffers.Binary;
using Voxa.Frames;
using Voxa.Pipelines;
using Voxa.Processors;
using Voxa.Speech;
using Voxa.Testing.Processors;

namespace Voxa.Speech.Abstractions.Tests;

public class SilenceGateProcessorTests
{
    private static (PipelineRunner Runner, CapturingProcessor Captured, Pipeline Pipeline) Build(double threshold = 0.01)
    {
        var captured = new CapturingProcessor();
        var pipeline = Pipeline.Build()
            .Source(new PipelineSource())
            .Then(new SilenceGateProcessor(threshold))
            .Then(captured)
            .Sink(new PipelineSink());
        return (new PipelineRunner(pipeline), captured, pipeline);
    }

    private static byte[] PcmAtAmplitude(int sampleCount, double amplitude)
    {
        // Generate a signed 16-bit constant-amplitude buffer at +amplitude.
        var bytes = new byte[sampleCount * 2];
        short sample = (short)(amplitude * short.MaxValue);
        for (int i = 0; i < sampleCount; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), sample);
        }
        return bytes;
    }

    [Fact]
    public void Rms_Of_Silence_Is_Zero()
    {
        Assert.Equal(0, SilenceGateProcessor.ComputeRms(new byte[100]), 6);
    }

    [Fact]
    public void Rms_Of_Constant_Half_Scale_Is_Half()
    {
        var pcm = PcmAtAmplitude(sampleCount: 100, amplitude: 0.5);
        Assert.Equal(0.5, SilenceGateProcessor.ComputeRms(pcm), 3);
    }

    [Fact]
    public void Empty_Pcm_Does_Not_Crash()
    {
        Assert.Equal(0, SilenceGateProcessor.ComputeRms(ReadOnlySpan<byte>.Empty));
        Assert.Equal(0, SilenceGateProcessor.ComputeRms(new byte[1])); // odd byte count
    }

    [Fact]
    public async Task Silent_AudioRawFrame_Is_Dropped()
    {
        var (runner, captured, pipeline) = Build(threshold: 0.01);
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(40);

            await pipeline.Source.IngestAsync(new AudioRawFrame(new byte[1600], 16000, 1));
            await Task.Delay(60);

            Assert.DoesNotContain(captured.Captured, f => f is AudioRawFrame);
        }
    }

    [Fact]
    public async Task Loud_AudioRawFrame_Is_Forwarded()
    {
        var (runner, captured, pipeline) = Build(threshold: 0.01);
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(40);

            await pipeline.Source.IngestAsync(new AudioRawFrame(PcmAtAmplitude(800, 0.5), 16000, 1));
            await Task.Delay(60);

            Assert.Contains(captured.Captured, f => f is AudioRawFrame);
        }
    }

    [Fact]
    public async Task Threshold_Is_Honoured_Above_And_Below()
    {
        // Threshold 0.10 — amplitude 0.05 below, 0.20 above
        var (runner, captured, pipeline) = Build(threshold: 0.10);
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(40);

            await pipeline.Source.IngestAsync(new AudioRawFrame(PcmAtAmplitude(800, 0.05), 16000, 1)); // dropped
            await pipeline.Source.IngestAsync(new AudioRawFrame(PcmAtAmplitude(800, 0.20), 16000, 1)); // forwarded
            await Task.Delay(80);

            var audioFrames = captured.Captured.OfType<AudioRawFrame>().ToList();
            Assert.Single(audioFrames);
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
            await captured.WaitForAsync(3, TimeSpan.FromSeconds(2)); // StartFrame + TextFrame + TranscriptionFrame

            Assert.Contains(captured.Captured, f => f is TextFrame);
            Assert.Contains(captured.Captured, f => f is TranscriptionFrame);
        }
    }
}
