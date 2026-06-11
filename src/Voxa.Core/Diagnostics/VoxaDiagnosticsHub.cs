using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Voxa.Diagnostics;

/// <summary>
/// Per-session diagnostics event stream (VST-001 WS0). Taps publish; renderers subscribe.
///
/// <para>
/// Contract with the pipeline: publishing must never slow it down. With no subscriber,
/// <see cref="Publish"/> is a single volatile bool check (publishers additionally guard event
/// construction on <see cref="HasListeners"/>, so idle cost is zero allocations). With
/// subscribers, each gets an independent bounded channel in drop-oldest mode — a slow renderer
/// loses old events (visible as <see cref="DiagnosticEvent.SeqNo"/> gaps), it never blocks audio.
/// </para>
///
/// <para>
/// Lifetime: one hub per voice session. Servers register it scoped (per connection);
/// Voxa Studio creates one per Talk session.
/// </para>
/// </summary>
public sealed class VoxaDiagnosticsHub
{
    /// <summary>Default per-subscriber channel capacity (~2 minutes of VAD windows).</summary>
    public const int DefaultChannelCapacity = 4096;

    private readonly object _gate = new();
    private readonly List<Channel<DiagnosticEvent>> _subscribers = new();
    private readonly int _capacity;
    private readonly long _origin = Stopwatch.GetTimestamp();
    private readonly StageLatencyTracker _tracker = new();
    private long _seqNo;
    private volatile bool _hasListeners;

    public VoxaDiagnosticsHub(int channelCapacity = DefaultChannelCapacity)
    {
        if (channelCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(channelCapacity), channelCapacity, "Capacity must be ≥ 1.");
        _capacity = channelCapacity;
    }

    /// <summary>
    /// True while at least one subscriber is attached. Publishers MUST guard event construction
    /// on this so an unobserved session allocates nothing.
    /// </summary>
    public bool HasListeners => _hasListeners;

    /// <summary>Monotonic microseconds since this hub was created — the session timebase.</summary>
    public long NowMicros() => (long)((Stopwatch.GetTimestamp() - _origin) * 1_000_000.0 / Stopwatch.Frequency);

    /// <summary>
    /// Stamp and fan out an event. Non-blocking: full subscriber channels drop their oldest
    /// event. Stage anchors additionally run through the built-in tracker, which derives
    /// <see cref="StageLatencyEvent"/>s and records the <c>voxa.stage.latency</c> histogram.
    /// </summary>
    public void Publish(DiagnosticEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (!_hasListeners) return;

        lock (_gate)
        {
            if (_subscribers.Count == 0) return; // raced an unsubscribe
            Dispatch(e);
            // Derived stage latencies ride the same ordered stream, after their anchor event.
            while (_tracker.Process(e) is { } derived)
            {
                Dispatch(derived);
                e = derived; // a derived event can itself anchor the next stage; re-feed it
            }
        }
    }

    private void Dispatch(DiagnosticEvent e)
    {
        e.SeqNo = ++_seqNo;
        e.TimestampMicros = e.TimestampMicros == 0 ? NowMicros() : e.TimestampMicros;
        foreach (var channel in _subscribers)
            channel.Writer.TryWrite(e); // bounded DropOldest — TryWrite always succeeds
    }

    /// <summary>
    /// Subscribe to the live event stream. Multiple concurrent subscribers are independent.
    /// The subscription detaches when the enumerator is disposed or <paramref name="ct"/> fires.
    /// </summary>
    public async IAsyncEnumerable<DiagnosticEvent> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateBounded<DiagnosticEvent>(new BoundedChannelOptions(_capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

        lock (_gate)
        {
            _subscribers.Add(channel);
            _hasListeners = true;
        }

        try
        {
            await foreach (var e in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return e;
        }
        finally
        {
            lock (_gate)
            {
                _subscribers.Remove(channel);
                _hasListeners = _subscribers.Count > 0;
            }
        }
    }
}
