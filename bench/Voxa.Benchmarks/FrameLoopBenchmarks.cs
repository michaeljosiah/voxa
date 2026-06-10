using System.Threading.Channels;
using BenchmarkDotNet.Attributes;
using Voxa.Frames;
using Voxa.Processors;

namespace Voxa.Benchmarks;

/// <summary>
/// Throughput and allocation of pumping frames through a linked processor pair. After WS1 the
/// per-frame linked CancellationTokenSource is gone, so the Allocated column should drop sharply
/// (only the channel async-wait floor remains).
/// </summary>
[MemoryDiagnoser]
public class FrameLoopBenchmarks
{
    private sealed class PassThrough : FrameProcessor
    {
        public PassThrough()
            : base("passthrough", new BoundedChannelOptions(16384) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true })
        { }

        protected override ValueTask ProcessFrameAsync(Frame frame, CancellationToken ct)
            => PushFrameAsync(frame, ct);
    }

    private PassThrough _a = null!;
    private PassThrough _b = null!;
    private AudioRawFrame _frame = null!;

    [GlobalSetup]
    public void Setup()
    {
        _a = new PassThrough();
        _b = new PassThrough();
        _a.Link(_b);
        _a.Start();
        _b.Start();
        _frame = new AudioRawFrame(new byte[640], 16000, 1);
    }

    [Benchmark(OperationsPerInvoke = 1000)]
    public async Task Pump1000Frames()
    {
        for (int i = 0; i < 1000; i++)
            await _a.QueueFrameAsync(_frame);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _a.DisposeAsync();
        await _b.DisposeAsync();
    }
}
