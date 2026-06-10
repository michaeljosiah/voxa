using Voxa.Frames;
using Voxa.Processors;

namespace Voxa.Core.Tests;

/// <summary>
/// Verifies the reusable-CTS data loop (VPS-001 WS1) preserves interruption semantics: an
/// interruption cancels the in-flight frame, and every subsequent frame still processes on a
/// fresh (uncancelled) token.
/// </summary>
public class FrameProcessorInterruptionTests
{
    /// <summary>
    /// Processor that blocks indefinitely (on the frame token) while <see cref="BlockData"/> is set,
    /// so a test can hold a data frame "in flight" and then preempt it with an interruption.
    /// </summary>
    private sealed class BlockingProcessor : FrameProcessor
    {
        public List<Frame> Completed { get; } = new();
        public volatile bool BlockData;

        public BlockingProcessor() : base("blocking") { }

        protected override async ValueTask ProcessFrameAsync(Frame frame, CancellationToken ct)
        {
            if (frame is AudioRawFrame && BlockData)
            {
                await Task.Delay(Timeout.Infinite, ct); // unblocked only by cancellation
            }
            lock (Completed) { Completed.Add(frame); }
            await PushFrameAsync(frame, ct);
        }
    }

    private static async Task WaitForAsync(BlockingProcessor p, Func<bool> cond, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            bool ok;
            lock (p.Completed) { ok = cond(); }
            if (ok) return;
            await Task.Delay(5);
        }
        throw new TimeoutException("condition not met within timeout");
    }

    private static AudioRawFrame Audio(byte tag) => new(new byte[] { tag, 0 }, 16000, 1);

    [Fact]
    public async Task Interruption_CancelsInFlightFrame_AndNextFrameStillProcesses()
    {
        await using var p = new BlockingProcessor { BlockData = true };
        p.Start();

        await p.QueueFrameAsync(Audio(1));     // blocks in flight
        await Task.Delay(50);

        // Interruption arrives on the system channel and cancels the in-flight frame's token.
        await p.QueueFrameAsync(new InterruptionFrame());
        await WaitForAsync(p, () => p.Completed.OfType<InterruptionFrame>().Any(), TimeSpan.FromSeconds(2));

        // The blocked audio frame must have been dropped (preempted), not completed.
        lock (p.Completed) Assert.DoesNotContain(p.Completed, f => f is AudioRawFrame);

        // A subsequent audio frame must process normally on a fresh, uncancelled token.
        p.BlockData = false;
        await p.QueueFrameAsync(Audio(2));
        await WaitForAsync(p, () => p.Completed.OfType<AudioRawFrame>().Any(), TimeSpan.FromSeconds(2));
        lock (p.Completed) Assert.Single(p.Completed.OfType<AudioRawFrame>());
    }

    [Fact]
    public async Task Interruption_BetweenFrames_DoesNotPoisonNextFrame()
    {
        await using var p = new BlockingProcessor { BlockData = false };
        p.Start();

        await p.QueueFrameAsync(Audio(1));
        await WaitForAsync(p, () => p.Completed.OfType<AudioRawFrame>().Count() == 1, TimeSpan.FromSeconds(2));

        await p.QueueFrameAsync(new InterruptionFrame()); // no frame in flight
        await WaitForAsync(p, () => p.Completed.OfType<InterruptionFrame>().Any(), TimeSpan.FromSeconds(2));

        await p.QueueFrameAsync(Audio(2));
        await WaitForAsync(p, () => p.Completed.OfType<AudioRawFrame>().Count() == 2, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task TwoInterruptions_BackToBack_SubsequentFramesProcess()
    {
        await using var p = new BlockingProcessor { BlockData = true };
        p.Start();

        await p.QueueFrameAsync(Audio(1));     // blocks
        await Task.Delay(40);
        await p.QueueFrameAsync(new InterruptionFrame());   // cancels frame 1
        await WaitForAsync(p, () => p.Completed.OfType<InterruptionFrame>().Count() == 1, TimeSpan.FromSeconds(2));

        await p.QueueFrameAsync(new InterruptionFrame());   // between frames (idle)
        await WaitForAsync(p, () => p.Completed.OfType<InterruptionFrame>().Count() == 2, TimeSpan.FromSeconds(2));

        p.BlockData = false;
        await p.QueueFrameAsync(Audio(2));
        await WaitForAsync(p, () => p.Completed.OfType<AudioRawFrame>().Any(), TimeSpan.FromSeconds(2));

        lock (p.Completed)
        {
            Assert.Equal(2, p.Completed.OfType<InterruptionFrame>().Count());
            Assert.Single(p.Completed.OfType<AudioRawFrame>());   // only frame 2; frame 1 was preempted
        }
    }
}
