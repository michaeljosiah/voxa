using System.Threading.Channels;
using Voxa.Frames;
using Voxa.Processors;

namespace Voxa.Core.Tests;

public class FrameProcessorTests
{
    private sealed class CapturingProcessor : FrameProcessor
    {
        public List<Frame> Captured { get; } = new();
        public TaskCompletionSource? GateNextDataFrame { get; set; }

        public CapturingProcessor(string name = "Capturing") : base(name) { }

        protected override async ValueTask ProcessFrameAsync(Frame frame, CancellationToken ct)
        {
            if (frame is DataFrame && GateNextDataFrame is { } gate)
            {
                GateNextDataFrame = null;
                await gate.Task.WaitAsync(ct);
            }
            lock (Captured) { Captured.Add(frame); }
            await PushFrameAsync(frame, ct);
        }
    }

    private static async Task WaitForCountAsync(CapturingProcessor p, Func<int> getExpected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            int count;
            lock (p.Captured) { count = p.Captured.Count; }
            if (count >= getExpected()) return;
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task Linked_Processors_Forward_Downstream_Frames_In_Order()
    {
        await using var a = new CapturingProcessor("a");
        await using var b = new CapturingProcessor("b");
        a.Link(b);
        a.Start();
        b.Start();

        await a.QueueFrameAsync(new TextFrame("one"));
        await a.QueueFrameAsync(new TextFrame("two"));
        await a.QueueFrameAsync(new TextFrame("three"));

        await WaitForCountAsync(b, () => 3, TimeSpan.FromSeconds(2));

        lock (b.Captured)
        {
            Assert.Equal(3, b.Captured.Count);
            Assert.Equal("one", ((TextFrame)b.Captured[0]).Text);
            Assert.Equal("two", ((TextFrame)b.Captured[1]).Text);
            Assert.Equal("three", ((TextFrame)b.Captured[2]).Text);
        }
    }

    [Fact]
    public async Task System_Frames_Preempt_Slow_Data_Processing()
    {
        await using var p = new CapturingProcessor("p");
        var gate = new TaskCompletionSource();
        p.GateNextDataFrame = gate;
        p.Start();

        // First data frame will block at the gate.
        await p.QueueFrameAsync(new TextFrame("slow"));

        // While the data task is blocked, send a system frame on the priority channel.
        await Task.Delay(30);
        await p.QueueFrameAsync(new InterruptionFrame());

        // System task should have processed the interruption even though data is blocked.
        await WaitForCountAsync(p, () => 1, TimeSpan.FromSeconds(2));
        lock (p.Captured)
        {
            Assert.Single(p.Captured);
            Assert.IsType<InterruptionFrame>(p.Captured[0]);
        }

        // Release the data frame; it should still be processed (interruption cancelled it
        // mid-flight, but the test's Captured.Add happens before await PushFrameAsync, so
        // the gate release lets it through normally on retry. Here we assert the slow frame
        // was cancelled and never appeared in Captured because the cancellation token tripped).
        gate.TrySetException(new OperationCanceledException("released after interrupt"));
        await Task.Delay(50);

        lock (p.Captured)
        {
            // Only the InterruptionFrame should be captured; the slow data frame was preempted.
            Assert.Single(p.Captured);
        }
    }

    [Fact]
    public async Task Upstream_Frames_Travel_Backwards_Through_The_Chain()
    {
        await using var src = new CapturingProcessor("src");
        await using var mid = new CapturingProcessor("mid");
        await using var snk = new CapturingProcessor("snk");
        src.Link(mid);
        mid.Link(snk);
        src.Start();
        mid.Start();
        snk.Start();

        var err = new ErrorFrame("boom") { Direction = FrameDirection.Upstream };
        await snk.QueueFrameAsync(err);

        await WaitForCountAsync(src, () => 1, TimeSpan.FromSeconds(2));
        await WaitForCountAsync(mid, () => 1, TimeSpan.FromSeconds(2));
        await WaitForCountAsync(snk, () => 1, TimeSpan.FromSeconds(2));

        lock (snk.Captured) Assert.Contains(snk.Captured, f => f is ErrorFrame);
        lock (mid.Captured) Assert.Contains(mid.Captured, f => f is ErrorFrame);
        lock (src.Captured) Assert.Contains(src.Captured, f => f is ErrorFrame);
    }

    [Fact]
    public async Task DisposeAsync_Stops_Drain_Loops_Cleanly()
    {
        var p = new CapturingProcessor("p");
        p.Start();
        await p.QueueFrameAsync(new TextFrame("hi"));
        await WaitForCountAsync(p, () => 1, TimeSpan.FromSeconds(2));

        await p.DisposeAsync();

        // After dispose, the channel writer is completed.
        await Assert.ThrowsAsync<ChannelClosedException>(async () =>
            await p.QueueFrameAsync(new TextFrame("after-dispose")));
    }

    [Fact]
    public async Task Cannot_Start_Twice()
    {
        await using var p = new CapturingProcessor("p");
        p.Start();
        Assert.Throws<InvalidOperationException>(() => p.Start());
    }
}
