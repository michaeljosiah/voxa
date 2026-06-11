using System.Buffers.Binary;
using Voxa.Audio.SileroVad;
using Voxa.Frames;
using Voxa.Pipelines;
using Voxa.Processors;
using Voxa.Testing.Processors;

namespace Voxa.Audio.SileroVad.Tests;

/// <summary>
/// Positive-detection regression tests against real recorded speech (the whisper.cpp jfk.wav
/// fixture). The original suite only asserted negatives (silence stays closed, mismatched rates
/// pass through), which masked a complete detection failure: Silero v5's ONNX contract prepends
/// 64 samples of context to each 512-sample window, and without it the model emits ~0.004
/// probability on clear speech. These tests pin the contract.
/// </summary>
public class SileroVadRealSpeechTests
{
    private static byte[] ReadWavPcm(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            var chunkId = System.Text.Encoding.ASCII.GetString(bytes, offset, 4);
            var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            if (chunkId == "data") return bytes.AsSpan(offset + 8, chunkSize).ToArray();
            offset += 8 + chunkSize + (chunkSize & 1);
        }
        throw new InvalidDataException($"{path} has no data chunk.");
    }

    private static float[] ToFloats(byte[] pcm)
    {
        var samples = new float[pcm.Length / 2];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2, 2)) / 32768f;
        return samples;
    }

    [Fact]
    public void Engine_Detects_Real_Speech_With_High_Confidence()
    {
        var samples = ToFloats(ReadWavPcm(Path.Combine(AppContext.BaseDirectory, "fixtures", "jfk.wav")));
        using var engine = new SileroVadEngine(16000);

        float maxProb = 0;
        int voiced = 0, windows = 0;
        for (int offset = 0; offset + engine.WindowSize <= samples.Length; offset += engine.WindowSize)
        {
            var prob = engine.Probability(samples.AsSpan(offset, engine.WindowSize));
            maxProb = Math.Max(maxProb, prob);
            if (prob >= 0.5f) voiced++;
            windows++;
        }

        // Without the v5 context contract this was maxProb ≈ 0.004 and voiced = 0.
        Assert.True(maxProb > 0.9f, $"max probability {maxProb:F3} — speech not detected");
        Assert.True(voiced > windows / 3, $"only {voiced}/{windows} voiced windows on continuous speech");
    }

    [Fact]
    public async Task Processor_Gate_Opens_And_Closes_On_Real_Speech()
    {
        var pcm = ReadWavPcm(Path.Combine(AppContext.BaseDirectory, "fixtures", "jfk.wav"));
        var captured = new CapturingProcessor();
        var pipeline = Pipeline.Build()
            .Source(new PipelineSource())
            .Then(new SileroVadProcessor(new SileroVadOptions
            {
                // The LowLatency profile's exact settings — what UseDefaults() composes.
                SampleRate = 16000,
                ConfidenceThreshold = 0.5f,
                MinRms = 0.003,
                StartDuration = TimeSpan.FromMilliseconds(150),
                StopDuration = TimeSpan.FromMilliseconds(400),
                PrerollDuration = TimeSpan.FromMilliseconds(300),
            }))
            .Then(captured)
            .Sink(new PipelineSink());

        await using var runner = new PipelineRunner(pipeline);
        await runner.StartAsync();

        const int frameBytes = 2 * 16000 / 50; // 20 ms frames, like a real transport
        for (int i = 0; i < pcm.Length; i += frameBytes)
        {
            var len = Math.Min(frameBytes, pcm.Length - i);
            await pipeline.Source.IngestAsync(new AudioRawFrame(pcm.AsMemory(i, len).ToArray(), 16000, 1));
        }
        var silence = new byte[frameBytes];
        for (int i = 0; i < 100; i++) // 2 s of silence so the gate closes
            await pipeline.Source.IngestAsync(new AudioRawFrame(silence, 16000, 1));

        await captured.WaitForAsync(f => f is UserStoppedSpeakingFrame, TimeSpan.FromSeconds(30));

        Assert.Contains(captured.Captured, f => f is UserStartedSpeakingFrame);
        Assert.Contains(captured.Captured, f => f is UserStoppedSpeakingFrame);
        // The gate forwarded the speech itself (plus preroll), not just the events.
        Assert.True(captured.Captured.Count(f => f is AudioRawFrame) > 100,
            $"only {captured.Captured.Count(f => f is AudioRawFrame)} audio frames passed the gate");
    }
}
