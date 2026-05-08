using Voxa.Frames;
using Voxa.Pipelines;
using Voxa.Testing.Audio;
using Voxa.Testing.Processors;

namespace Voxa.Core.Tests;

public class WavRoundtripTests
{
    [Fact]
    public async Task Wav_File_Source_To_Sink_Preserves_Audio_Bytewise()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"voxa-wav-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var input = Path.Combine(dir, "input.wav");
        var output = Path.Combine(dir, "output.wav");

        try
        {
            // 250ms of 440 Hz sine at 24 kHz mono — enough samples to span many chunks.
            var pcm = GenerateSineWave(sampleRate: 24000, channels: 1, durationMs: 250, frequencyHz: 440);
            WavFile.Write(input, pcm, 24000, 1);

            var pipeline = Pipeline.Build()
                .Source(new WavFileSourceProcessor(input, frameDurationMs: 20))
                .Then(new PassthroughProcessor())
                .Sink(new WavFileSinkProcessor(output));

            await using var runner = new PipelineRunner(pipeline);
            await runner.StartAsync();

            await runner.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5));

            var read = WavFile.Read(output);
            Assert.Equal(24000, read.SampleRate);
            Assert.Equal(1, read.Channels);
            Assert.Equal(pcm.Length, read.Pcm.Length);
            Assert.Equal(pcm, read.Pcm);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Wav_Roundtrip_Of_Stereo_44100_Preserves_Format()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"voxa-wav-fmt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "stereo.wav");
        try
        {
            var pcm = new byte[44100 * 2 * 2 / 10];     // 100 ms of 44.1 kHz stereo
            new Random(42).NextBytes(pcm);
            WavFile.Write(path, pcm, 44100, 2);

            var read = WavFile.Read(path);
            Assert.Equal(44100, read.SampleRate);
            Assert.Equal(2, read.Channels);
            Assert.Equal(pcm, read.Pcm);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static byte[] GenerateSineWave(int sampleRate, int channels, int durationMs, double frequencyHz)
    {
        int sampleCount = (sampleRate * durationMs) / 1000;
        var samples = new short[sampleCount * channels];
        double phase = 0;
        double phaseInc = 2 * Math.PI * frequencyHz / sampleRate;
        for (int i = 0; i < sampleCount; i++)
        {
            short sample = (short)(short.MaxValue * 0.3 * Math.Sin(phase));
            for (int c = 0; c < channels; c++) samples[i * channels + c] = sample;
            phase += phaseInc;
        }

        var bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
