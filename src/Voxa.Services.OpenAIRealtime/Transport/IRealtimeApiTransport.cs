namespace Voxa.Services.OpenAIRealtime.Transport;

/// <summary>
/// Wire-level transport for the OpenAI Realtime protocol. The transport handles connection and
/// message framing only — JSON encoding/decoding lives in the processor + codec.
/// </summary>
public interface IRealtimeApiTransport : IAsyncDisposable
{
    /// <summary>Open the underlying connection.</summary>
    Task ConnectAsync(CancellationToken ct);

    /// <summary>Send one JSON event upstream.</summary>
    ValueTask SendEventAsync(string json, CancellationToken ct);

    /// <summary>Stream of JSON events received from the server. Completes when the connection closes.</summary>
    IAsyncEnumerable<string> ReadEventsAsync(CancellationToken ct);
}
