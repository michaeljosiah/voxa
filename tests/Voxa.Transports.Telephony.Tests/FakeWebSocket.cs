using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace Voxa.Transports.Telephony.Tests;

/// <summary>
/// In-memory <see cref="System.Net.WebSockets.WebSocket"/> for telephony tests. Captures every send;
/// lets tests inject scripted receive frames via <see cref="QueueIncomingTextAsync"/>. Mirrors the
/// fake used by the native WebSocket transport tests.
/// </summary>
internal sealed class FakeWebSocket : System.Net.WebSockets.WebSocket
{
    private readonly Channel<(WebSocketMessageType Type, byte[] Data, bool EndOfMessage)> _incoming =
        Channel.CreateUnbounded<(WebSocketMessageType, byte[], bool)>();

    private readonly List<(WebSocketMessageType Type, byte[] Data, bool EndOfMessage)> _sent = new();
    private readonly object _sentLock = new();
    private WebSocketState _state = WebSocketState.Open;
    private volatile TaskCompletionSource? _sendGate;

    /// <summary>Block every subsequent <see cref="SendAsync"/> until <see cref="ReleaseSends"/>.</summary>
    public void BlockSends() => _sendGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Release a gate set by <see cref="BlockSends"/>; later sends are not blocked.</summary>
    public void ReleaseSends()
    {
        var gate = _sendGate;
        _sendGate = null;
        gate?.TrySetResult();
    }

    public IReadOnlyList<string> SentTextAsString
    {
        get { lock (_sentLock) return _sent.Where(s => s.Type == WebSocketMessageType.Text).Select(s => Encoding.UTF8.GetString(s.Data)).ToList(); }
    }

    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override WebSocketState State => _state;
    public override string? SubProtocol => null;

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    { _state = WebSocketState.Closed; return Task.CompletedTask; }

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    { _state = WebSocketState.CloseSent; return Task.CompletedTask; }

    public override void Abort() { _state = WebSocketState.Aborted; }

    public override void Dispose() { _state = WebSocketState.Closed; }

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        var msg = await _incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var len = Math.Min(msg.Data.Length, buffer.Count);
        Buffer.BlockCopy(msg.Data, 0, buffer.Array!, buffer.Offset, len);
        return new WebSocketReceiveResult(len, msg.Type, msg.EndOfMessage);
    }

    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        var gate = _sendGate;
        if (gate is not null) await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_sentLock) _sent.Add((messageType, buffer.ToArray(), endOfMessage));
    }

    public ValueTask QueueIncomingTextAsync(string json, bool endOfMessage = true)
        => _incoming.Writer.WriteAsync((WebSocketMessageType.Text, Encoding.UTF8.GetBytes(json), endOfMessage));

    /// <summary>Queue a Close frame so the source's read loop ingests an EndFrame and exits.</summary>
    public ValueTask QueueCloseAsync()
        => _incoming.Writer.WriteAsync((WebSocketMessageType.Close, Array.Empty<byte>(), true));

    public void CompleteIncoming() => _incoming.Writer.TryComplete();

    /// <summary>Poll until <paramref name="predicate"/> matches one of the sent text frames or timeout.</summary>
    public async Task<string?> WaitForSentTextAsync(Func<string, bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var match = SentTextAsString.FirstOrDefault(predicate);
            if (match is not null) return match;
            await Task.Delay(10);
        }
        return null;
    }

    /// <summary>Count sent text frames matching <paramref name="predicate"/>.</summary>
    public int CountSentText(Func<string, bool> predicate)
        => SentTextAsString.Count(predicate);
}
