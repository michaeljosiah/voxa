using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Voxa.Services.AzureVoiceLive.Transport;

namespace Voxa.Services.AzureVoiceLive.Tests;

/// <summary>
/// In-memory <see cref="IRealtimeApiTransport"/> for offline tests. Captures every event the
/// processor sends; lets tests script server events via <see cref="QueueServerEventAsync"/>.
/// </summary>
internal sealed class ScriptedRealtimeApiTransport : IRealtimeApiTransport
{
    private readonly Channel<string> _serverEvents = Channel.CreateUnbounded<string>();
    private readonly List<string> _sent = new();
    private readonly object _lock = new();

    public bool Connected { get; private set; }

    public IReadOnlyList<string> SentEvents
    {
        get { lock (_lock) return _sent.ToList(); }
    }

    public Task ConnectAsync(CancellationToken ct)
    {
        Connected = true;
        return Task.CompletedTask;
    }

    public ValueTask SendEventAsync(string json, CancellationToken ct)
    {
        lock (_lock) _sent.Add(json);
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<string> ReadEventsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var evt in _serverEvents.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return evt;
        }
    }

    public ValueTask QueueServerEventAsync(string json) => _serverEvents.Writer.WriteAsync(json);

    public ValueTask DisposeAsync()
    {
        Connected = false;
        _serverEvents.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
