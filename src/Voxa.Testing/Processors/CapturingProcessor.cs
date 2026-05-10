using Voxa.Frames;
using Voxa.Processors;

namespace Voxa.Testing.Processors;

/// <summary>
/// Records every frame it observes and forwards it unchanged. Use mid-pipeline in tests to
/// assert what flows through a particular point.
/// </summary>
public sealed class CapturingProcessor : FrameProcessor
{
    private readonly List<Frame> _captured = new();
    private readonly object _lock = new();

    public CapturingProcessor(string name = "Capturing") : base(name) { }

    /// <summary>Snapshot of all frames observed so far. Thread-safe.</summary>
    public IReadOnlyList<Frame> Captured
    {
        get { lock (_lock) return _captured.ToList(); }
    }

    /// <summary>Wait until the captured count reaches <paramref name="expected"/> or timeout.</summary>
    public async Task WaitForAsync(int expected, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            int count;
            lock (_lock) { count = _captured.Count; }
            if (count >= expected) return;
            await Task.Delay(5, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Wait until at least one captured frame matches <paramref name="predicate"/> or timeout.
    /// Useful for awaiting a specific frame type or content rather than a raw count.
    /// </summary>
    public async Task WaitForAsync(Func<Frame, bool> predicate, TimeSpan timeout, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            bool any;
            lock (_lock) { any = _captured.Any(predicate); }
            if (any) return;
            await Task.Delay(5, ct).ConfigureAwait(false);
        }
    }

    protected override async ValueTask ProcessFrameAsync(Frame frame, CancellationToken ct)
    {
        lock (_lock) _captured.Add(frame);
        await PushFrameAsync(frame, ct).ConfigureAwait(false);
    }
}
