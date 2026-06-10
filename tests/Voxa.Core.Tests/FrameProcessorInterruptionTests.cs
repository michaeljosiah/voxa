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

    /// <summary>
    /// Processor that captures the token handed to OnStartAsync and can gate uninterruptible
    /// tool-result frames, for asserting which token classes an interruption may cancel.
    /// </summary>
    private sealed class SeamProcessor : FrameProcessor
    {
        public CancellationToken StartToken { get; private set; }
        public volatile bool BlockAudio;
        public TaskCompletionSource? ToolGate;
        public List<Frame> Completed { get; } = new();

        public SeamProcessor() : base("seam") { }

        protected override ValueTask OnStartAsync(StartFrame frame, CancellationToken ct)
        {
            StartToken = ct;
            return ValueTask.CompletedTask;
        }

        protected override async ValueTask ProcessFrameAsync(Frame frame, CancellationToken ct)
        {
            if (frame is AudioRawFrame && BlockAudio)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            if (frame is ToolCallResultFrame && ToolGate is { } gate)
            {
                // If an interruption wrongly cancels this uninterruptible frame's token, this
                // throws OCE and the frame never reaches Completed.
                await gate.Task.WaitAsync(ct);
            }
            lock (Completed) { Completed.Add(frame); }
        }
    }

    private static async Task WaitForSeamAsync(SeamProcessor p, Func<bool> cond, TimeSpan timeout)
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

    [Fact]
    public async Task Interruption_DoesNotCancel_TokenGivenToOnStart()
    {
        // Regression for VPS-001 WS1: processors start long-lived pumps (WebSocket read/write
        // loops) from OnStartAsync. With the shared frame CTS, an interruption that preempts a
        // data frame must NOT also fire the token those pumps were started with.
        await using var p = new SeamProcessor();
        p.Start();

        await p.QueueFrameAsync(new StartFrame());
        await WaitForSeamAsync(p, () => p.Completed.OfType<StartFrame>().Any(), TimeSpan.FromSeconds(2));

        p.BlockAudio = true;
        await p.QueueFrameAsync(Audio(1));        // in flight — interruption will fire the shared CTS
        await Task.Delay(50);
        await p.QueueFrameAsync(new InterruptionFrame());
        await WaitForSeamAsync(p, () => p.Completed.OfType<InterruptionFrame>().Any(), TimeSpan.FromSeconds(2));

        Assert.False(p.StartToken.IsCancellationRequested,
            "OnStartAsync's token was cancelled by an interruption — background pumps started there would have died");
    }

    [Fact]
    public async Task Interruption_DoesNotCancel_UninterruptibleFrame()
    {
        // IUninterruptible contract: an in-flight ToolCallResultFrame must survive an interruption.
        await using var p = new SeamProcessor { ToolGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        p.Start();

        await p.QueueFrameAsync(new ToolCallResultFrame("c1", "{}"));   // blocks at the gate
        await Task.Delay(50);
        await p.QueueFrameAsync(new InterruptionFrame());
        await WaitForSeamAsync(p, () => p.Completed.OfType<InterruptionFrame>().Any(), TimeSpan.FromSeconds(2));

        p.ToolGate!.TrySetResult();   // release — the frame must complete, not have been dropped
        await WaitForSeamAsync(p, () => p.Completed.OfType<ToolCallResultFrame>().Any(), TimeSpan.FromSeconds(2));
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
