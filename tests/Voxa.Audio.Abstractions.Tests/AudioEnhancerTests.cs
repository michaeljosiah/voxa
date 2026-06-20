using Voxa.Audio;
using Voxa.Frames;
using Voxa.Testing.Processors;

namespace Voxa.Audio.Abstractions.Tests;

/// <summary>
/// Records Reset/Dispose and XORs each byte with 0xFF on <see cref="Enhance"/>, so a test can prove the
/// processor emits the cleaned buffer (not the input) without any real model.
/// </summary>
internal sealed class FakeAudioEnhancer : IAudioEnhancer
{
    public FakeAudioEnhancer(int sampleRate = 16000) => SampleRate = sampleRate;

    public int SampleRate { get; }
    public int Resets { get; private set; }
    public bool Disposed { get; private set; }

    public ReadOnlyMemory<byte> Enhance(ReadOnlyMemory<byte> pcm)
    {
        var outp = pcm.ToArray();
        for (var i = 0; i < outp.Length; i++) outp[i] ^= 0xFF; // mark: cleaned != input, same length
        return outp;
    }

    public void Reset() => Resets++;
    public void Dispose() => Disposed = true;
}

public class NullAudioEnhancerTests
{
    [Fact]
    public void Enhance_Is_Passthrough_Same_Buffer()
    {
        var pcm = new byte[] { 1, 2, 3, 4 };
        ReadOnlyMemory<byte> input = pcm;

        var output = new NullAudioEnhancer(16000).Enhance(input);

        Assert.True(output.Equals(input)); // unchanged, no copy
    }

    [Fact]
    public void Reset_And_Dispose_Are_NoOps()
    {
        var e = new NullAudioEnhancer(16000);
        e.Reset();   // must not throw
        e.Dispose(); // must not throw
        Assert.Equal(16000, e.SampleRate);
    }
}

public class AudioEnhancerProcessorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task Cleans_Audio_And_Forwards_Other_Frames()
    {
        var fake = new FakeAudioEnhancer();
        await using var enh = new AudioEnhancerProcessor(fake);
        await using var sink = new CapturingProcessor();
        enh.Link(sink);
        enh.Start();
        sink.Start();

        await enh.QueueFrameAsync(new StartFrame(16000, 1));
        await enh.QueueFrameAsync(new AudioRawFrame(new byte[] { 0x00, 0x01, 0x02 }, 16000, 1));
        await enh.QueueFrameAsync(new EndFrame());

        await sink.WaitForAsync(f => f is EndFrame, Timeout);

        var frames = sink.Captured;
        Assert.Contains(frames, f => f is StartFrame);
        Assert.Contains(frames, f => f is EndFrame);
        var cleaned = (AudioRawFrame)frames.First(f => f is AudioRawFrame);
        Assert.Equal(new byte[] { 0xFF, 0xFE, 0xFD }, cleaned.Pcm.ToArray()); // enhanced (XOR 0xFF), not the input
    }

    [Fact]
    public async Task Resets_On_Start_And_Disposes_On_End()
    {
        var fake = new FakeAudioEnhancer();
        await using var enh = new AudioEnhancerProcessor(fake);
        await using var sink = new CapturingProcessor();
        enh.Link(sink);
        enh.Start();
        sink.Start();

        await enh.QueueFrameAsync(new StartFrame(16000, 1));
        await enh.QueueFrameAsync(new EndFrame());
        await sink.WaitForAsync(f => f is EndFrame, Timeout);
        await Task.Delay(30);

        Assert.True(fake.Resets >= 1); // reset for the fresh session
        Assert.True(fake.Disposed);    // model/DSP released on end
    }

    [Fact]
    public async Task Mismatched_Sample_Rate_Fails_Fast_Without_Enhancing_Or_Forwarding()
    {
        // VLS-004 (Codex P2): a rate mismatch is a config error → fail fast. The processor surfaces an upstream
        // ErrorFrame (PipelineRunner turns it into a session failure) and never feeds wrong-rate PCM to the
        // model or forwards it onward (so the VAD/STT never see corrupted audio).
        var fake = new FakeAudioEnhancer(16000); // expects 16 kHz
        await using var upstream = new CapturingProcessor("up");
        await using var enh = new AudioEnhancerProcessor(fake);
        await using var sink = new CapturingProcessor("down");
        upstream.Link(enh);
        enh.Link(sink);
        upstream.Start();
        enh.Start();
        sink.Start();

        await enh.QueueFrameAsync(new AudioRawFrame(new byte[] { 1, 2, 3, 4 }, 24000, 1)); // 24 kHz ≠ 16 kHz

        await upstream.WaitForAsync(f => f is ErrorFrame, Timeout);
        await Task.Delay(40);

        Assert.Contains(upstream.Captured, f => f is ErrorFrame);          // mismatch surfaced (fails the session)
        Assert.DoesNotContain(sink.Captured, f => f is AudioRawFrame);     // wrong-rate audio never enhanced/forwarded
    }

    [Fact]
    public async Task Null_Enhancer_Leaves_Audio_Byte_Identical()
    {
        await using var enh = new AudioEnhancerProcessor(new NullAudioEnhancer(16000));
        await using var sink = new CapturingProcessor();
        enh.Link(sink);
        enh.Start();
        sink.Start();

        var pcm = new byte[] { 5, 6, 7, 8 };
        await enh.QueueFrameAsync(new AudioRawFrame(pcm, 16000, 1));

        await sink.WaitForAsync(f => f is AudioRawFrame, Timeout);

        var forwarded = (AudioRawFrame)sink.Captured.First(f => f is AudioRawFrame);
        Assert.Equal(pcm, forwarded.Pcm.ToArray()); // passthrough
    }
}
