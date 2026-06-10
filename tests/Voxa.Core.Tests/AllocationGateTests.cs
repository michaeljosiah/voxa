using System.Threading.Channels;
using Voxa.Frames;
using Voxa.Processors;

namespace Voxa.Core.Tests;

/// <summary>
/// Allocation regression gate for the pipeline hot loop (VPS-001 WS0.2 / WS1). Asserts that
/// steady-state frame processing does not allocate per frame. Before WS1 each frame allocated a
/// linked <see cref="CancellationTokenSource"/> on the drain thread (~150+ B/frame); after WS1 the
/// CTS is reused and steady-state drain allocation is ≈ 0.
/// </summary>
public class AllocationGateTests
{
    private sealed class CountingProcessor : FrameProcessor
    {
        private int _seen;
        public int Seen => Volatile.Read(ref _seen);

        // Oversized data channel so the producer never blocks during the measurement window —
        // this isolates DRAIN-thread allocations (the CTS we removed) from producer backpressure.
        public CountingProcessor()
            : base("counting", new BoundedChannelOptions(16384) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true })
        { }

        protected override ValueTask ProcessFrameAsync(Frame frame, CancellationToken ct)
        {
            Interlocked.Increment(ref _seen);
            return ValueTask.CompletedTask;
        }
    }

    private static async Task PumpAsync(CountingProcessor p, AudioRawFrame frame, int count)
    {
        for (int i = 0; i < count; i++) await p.QueueFrameAsync(frame);
    }

    private static async Task DrainedAsync(CountingProcessor p, int target)
    {
        for (int i = 0; i < 1000 && p.Seen < target; i++) await Task.Delay(5);
        Assert.True(p.Seen >= target, $"only {p.Seen}/{target} frames drained");
    }

    [Fact]
    public async Task DataLoop_SteadyState_DoesNotAllocatePerFrame()
    {
        await using var p = new CountingProcessor();
        p.Start();
        var frame = new AudioRawFrame(new byte[640], 16000, 1);

        // Warm up the JIT, channel internals, and one-time async-enumerator state machines.
        await PumpAsync(p, frame, 2000);
        await DrainedAsync(p, 2000);

        // There is an irreducible ~100 B/frame floor from System.Threading.Channels' async-wait
        // machinery (allocated whenever the reader parks waiting for the next write) — it is
        // present with or without WS1. The CTS we removed cost a further ~150 B/frame on top of
        // that (a linked CancellationTokenSource + its parent-token registration). Budget 160 sits
        // safely above the channel floor yet well below the ~250 B/frame a reintroduced
        // per-frame CTS would produce, so this gate fails if WS1 regresses.
        //
        // GetTotalAllocatedBytes is process-wide and xUnit runs other test classes in parallel,
        // so a single window can be inflated by unrelated tests' allocations. That noise is
        // additive-only: the MINIMUM across trials approximates the true drain-loop cost, while
        // a real per-frame CTS regression raises every trial.
        const int N = 10000;
        const double Budget = 160;
        double best = double.MaxValue;
        int pumped = 2000;
        for (int trial = 0; trial < 3 && best >= Budget; trial++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long before = GC.GetTotalAllocatedBytes(precise: true);   // all threads, incl. the drain loop
            await PumpAsync(p, frame, N);
            await DrainedAsync(p, pumped + N);
            long after = GC.GetTotalAllocatedBytes(precise: true);

            pumped += N;
            best = Math.Min(best, (after - before) / (double)N);
        }

        Assert.True(best < Budget,
            $"Steady-state allocated {best:F1} B/frame (best of trials; budget {Budget}). Per-frame CTS regression?");
    }
}
