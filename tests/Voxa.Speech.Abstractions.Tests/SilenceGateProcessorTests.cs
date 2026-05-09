using System.Buffers.Binary;
using Voxa.Frames;
using Voxa.Pipelines;
using Voxa.Processors;
using Voxa.Speech;
using Voxa.Testing.Processors;

namespace Voxa.Speech.Abstractions.Tests;

public class SilenceGateProcessorTests
{
    private static (PipelineRunner Runner, CapturingProcessor Captured, Pipeline Pipeline) Build(
        double threshold = 0.005,
        TimeSpan? hangover = null)
    {
        var captured = new CapturingProcessor();
        var pipeline = Pipeline.Build()
            .Source(new PipelineSource())
            .Then(new SilenceGateProcessor(threshold, hangover))
            .Then(captured)
            .Sink(new PipelineSink());
        return (new PipelineRunner(pipeline), captured, pipeline);
    }

    private static byte[] PcmAtAmplitude(int sampleCount, double amplitude)
    {
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
        Assert.Equal(0, SilenceGateProcessor.ComputeRms(new byte[1]));
    }

    [Fact]
    public async Task Silent_Frame_Before_Any_Speech_Is_Dropped()
    {
        var (runner, captured, pipeline) = Build();
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
    public async Task Loud_Frame_Opens_Gate_And_Is_Forwarded()
    {
        var (runner, captured, pipeline) = Build();
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
    public async Task Hangover_Keeps_Gate_Open_For_Briefly_Quiet_Frames_After_Speech()
    {
        // 200 ms hangover. Loud frame opens the gate; subsequent quiet frame within 200 ms
        // should still be forwarded (syllable gap), but a quiet frame after the hangover
        // expires should be dropped again.
        var (runner, captured, pipeline) = Build(threshold: 0.05, hangover: TimeSpan.FromMilliseconds(200));
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(40);

            await pipeline.Source.IngestAsync(new AudioRawFrame(PcmAtAmplitude(800, 0.5), 16000, 1));   // loud — opens
            await Task.Delay(50);
            await pipeline.Source.IngestAsync(new AudioRawFrame(new byte[1600], 16000, 1));             // quiet, within hangover — passes
            await Task.Delay(60);

            int countWhileOpen = captured.Captured.OfType<AudioRawFrame>().Count();
            Assert.Equal(2, countWhileOpen);

            // Wait past the hangover window, then send another quiet frame — should be dropped.
            await Task.Delay(250);
            await pipeline.Source.IngestAsync(new AudioRawFrame(new byte[1600], 16000, 1));
            await Task.Delay(60);

            int countAfterHangover = captured.Captured.OfType<AudioRawFrame>().Count();
            Assert.Equal(2, countAfterHangover); // unchanged
        }
    }

    [Fact]
    public async Task Threshold_Is_Honoured_Above_And_Below()
    {
        // Threshold 0.10, hangover effectively zero so each frame is judged independently
        // (still need hangover > 0 since DateTime.UtcNow can compare equal; use 1ms).
        var (runner, captured, pipeline) = Build(threshold: 0.10, hangover: TimeSpan.FromMilliseconds(1));
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(40);

            await pipeline.Source.IngestAsync(new AudioRawFrame(PcmAtAmplitude(800, 0.05), 16000, 1)); // dropped
            await Task.Delay(20);
            await pipeline.Source.IngestAsync(new AudioRawFrame(PcmAtAmplitude(800, 0.20), 16000, 1)); // forwarded
            await Task.Delay(40);

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
            await captured.WaitForAsync(3, TimeSpan.FromSeconds(2));

            Assert.Contains(captured.Captured, f => f is TextFrame);
            Assert.Contains(captured.Captured, f => f is TranscriptionFrame);
        }
    }
}
