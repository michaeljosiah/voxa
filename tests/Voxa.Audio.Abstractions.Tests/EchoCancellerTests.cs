using Voxa.Audio;
using Voxa.Frames;
using Voxa.Testing.Processors;

namespace Voxa.Audio.Abstractions.Tests;

/// <summary>
/// Records every interaction in call order so tests can assert ordering, counts, and that the
/// near-end <c>CancelEcho</c> and far-end <c>FeedReference</c> hit the same shared instance.
/// <c>CancelEcho</c> XORs each byte with 0xFF so a test can prove the processor emits the cleaned
/// buffer (not the input) without depending on any real DSP.
/// </summary>
internal sealed class FakeEchoCanceller : IEchoCanceller
{
    private readonly object _lock = new();
    private readonly List<string> _calls = new();

    public FakeEchoCanceller(int sampleRate = 16000) => SampleRate = sampleRate;

    public int SampleRate { get; }
    public int Resets { get; private set; }

    public void FeedReference(ReadOnlyMemory<byte> farEndPcm)
    {
        lock (_lock) _calls.Add($"feed:{farEndPcm.Length}");
    }

    public ReadOnlyMemory<byte> CancelEcho(ReadOnlyMemory<byte> nearEndPcm)
    {
        lock (_lock) _calls.Add($"cancel:{nearEndPcm.Length}");
        var outp = nearEndPcm.ToArray();
        for (var i = 0; i < outp.Length; i++) outp[i] ^= 0xFF; // mark: cleaned != input
        return outp;
    }

    public void Reset()
    {
        lock (_lock) { _calls.Add("reset"); Resets++; }
    }

    public IReadOnlyList<string> Calls
    {
        get { lock (_lock) return _calls.ToList(); }
    }
}

public class NullEchoCancellerTests
{
    [Fact]
    public void CancelEcho_Is_Passthrough_Same_Buffer()
    {
        var pcm = new byte[] { 1, 2, 3, 4 };
        ReadOnlyMemory<byte> input = pcm;

        var output = NullEchoCanceller.Instance.CancelEcho(input);

        // Returns the input untouched (same wrapped array+range) — no copy, byte-identical.
        Assert.True(output.Equals(input));
    }

    [Fact]
    public void FeedReference_And_Reset_Are_NoOps()
    {
        var c = NullEchoCanceller.Instance;

        c.FeedReference(new byte[] { 9, 9 }); // must not throw
        c.Reset();                            // must not throw

        Assert.Equal(0, c.SampleRate); // rate-agnostic
    }
}

public class EchoCancellerProcessorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task Cleans_Audio_And_Forwards_Other_Frames()
    {
        var fake = new FakeEchoCanceller();
        await using var aec = new EchoCancellerProcessor(fake);
        await using var sink = new CapturingProcessor();
        aec.Link(sink);
        aec.Start();
        sink.Start();

        await aec.QueueFrameAsync(new StartFrame(16000, 1));
        await aec.QueueFrameAsync(new AudioRawFrame(new byte[] { 0x00, 0x01, 0x02 }, 16000, 1));
        await aec.QueueFrameAsync(new EndFrame());

        await sink.WaitForAsync(f => f is EndFrame, Timeout);

        var frames = sink.Captured;
        Assert.Contains(frames, f => f is StartFrame);
        Assert.Contains(frames, f => f is EndFrame);

        // The audio frame carries the cleaned PCM (each byte XOR 0xFF), not the input.
        var cleaned = (AudioRawFrame)frames.First(f => f is AudioRawFrame);
        Assert.Equal(new byte[] { 0xFF, 0xFE, 0xFD }, cleaned.Pcm.ToArray());

        // CancelEcho ran exactly once — only for the AudioRawFrame, never for Start/End.
        Assert.Equal(1, fake.Calls.Count(c => c.StartsWith("cancel")));
    }

    [Fact]
    public async Task Resets_On_Start_And_On_Interruption()
    {
        var fake = new FakeEchoCanceller();
        await using var aec = new EchoCancellerProcessor(fake);
        await using var sink = new CapturingProcessor();
        aec.Link(sink);
        aec.Start();
        sink.Start();

        await aec.QueueFrameAsync(new StartFrame(16000, 1)); // reset #1 (session start)
        await aec.QueueFrameAsync(new InterruptionFrame());  // reset #2 (barge-in epoch)

        await sink.WaitForAsync(f => f is InterruptionFrame, Timeout);

        Assert.True(fake.Resets >= 2, $"expected ≥2 resets, saw {fake.Resets}");
        Assert.Contains(sink.Captured, f => f is InterruptionFrame); // forwarded downstream too
    }

    [Fact]
    public async Task Null_Canceller_Leaves_Audio_Byte_Identical()
    {
        await using var aec = new EchoCancellerProcessor(NullEchoCanceller.Instance);
        await using var sink = new CapturingProcessor();
        aec.Link(sink);
        aec.Start();
        sink.Start();

        var pcm = new byte[] { 5, 6, 7, 8 };
        await aec.QueueFrameAsync(new AudioRawFrame(pcm, 16000, 1));

        await sink.WaitForAsync(f => f is AudioRawFrame, Timeout);

        var forwarded = (AudioRawFrame)sink.Captured.First(f => f is AudioRawFrame);
        Assert.Equal(pcm, forwarded.Pcm.ToArray()); // passthrough: unchanged
    }
}

public class EchoReferenceTapProcessorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task Feeds_FarEnd_And_Forwards_Unchanged()
    {
        var fake = new FakeEchoCanceller(24000);
        await using var tap = new EchoReferenceTapProcessor(fake);
        await using var sink = new CapturingProcessor();
        tap.Link(sink);
        tap.Start();
        sink.Start();

        var bot = new AudioRawFrame(new byte[] { 7, 8, 9, 10 }, 24000, 1);
        await tap.QueueFrameAsync(bot);

        await sink.WaitForAsync(f => f is AudioRawFrame, Timeout);

        // FeedReference saw the bot audio; the tap never cancels; the frame forwards unchanged.
        Assert.Contains("feed:4", fake.Calls);
        Assert.Equal(0, fake.Calls.Count(c => c.StartsWith("cancel")));
        var forwarded = (AudioRawFrame)sink.Captured.First(f => f is AudioRawFrame);
        Assert.True(forwarded.Pcm.Equals(bot.Pcm)); // same buffer, observe-only
    }
}

public class SharedCancellerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task One_Canceller_Serves_Both_NearEnd_Cancel_And_FarEnd_Feed()
    {
        // The composer's design: one scoped IEchoCanceller, handed to both the near-end processor
        // and the far-end tap. Prove a single instance records both sides.
        var fake = new FakeEchoCanceller();

        await using var nearEnd = new EchoCancellerProcessor(fake);
        await using var nearSink = new CapturingProcessor();
        nearEnd.Link(nearSink);
        nearEnd.Start();
        nearSink.Start();

        await using var farEnd = new EchoReferenceTapProcessor(fake);
        await using var farSink = new CapturingProcessor();
        farEnd.Link(farSink);
        farEnd.Start();
        farSink.Start();

        await nearEnd.QueueFrameAsync(new AudioRawFrame(new byte[] { 1, 2 }, 16000, 1));     // mic
        await farEnd.QueueFrameAsync(new AudioRawFrame(new byte[] { 3, 4, 5 }, 16000, 1));   // bot

        await nearSink.WaitForAsync(f => f is AudioRawFrame, Timeout);
        await farSink.WaitForAsync(f => f is AudioRawFrame, Timeout);

        var calls = fake.Calls;
        Assert.Contains("cancel:2", calls); // near-end mic cancelled
        Assert.Contains("feed:3", calls);   // far-end bot fed
    }
}
