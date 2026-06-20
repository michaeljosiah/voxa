using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;

namespace Voxa.Services.AzureVoiceLive.Transport;

/// <summary>
/// Default <see cref="IRealtimeApiTransport"/> backed by <see cref="ClientWebSocket"/>. Suitable
/// for Azure Voice Live, Azure OpenAI Realtime, and OpenAI Realtime endpoints — only the URL
/// and auth header differ.
/// </summary>
public sealed class WebSocketRealtimeApiTransport : IRealtimeApiTransport
{
    private readonly Uri _endpoint;
    private readonly string _apiKey;
    private readonly string _apiKeyHeaderName;
    private readonly ClientWebSocket _ws = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>
    /// Construct a transport. <paramref name="apiKeyHeaderName"/> defaults to <c>api-key</c> for
    /// Azure; pass <c>Authorization</c> with a <c>Bearer ...</c> value for OpenAI.
    /// </summary>
    public WebSocketRealtimeApiTransport(Uri endpoint, string apiKey, string apiKeyHeaderName = "api-key")
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _apiKeyHeaderName = apiKeyHeaderName ?? throw new ArgumentNullException(nameof(apiKeyHeaderName));
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        _ws.Options.SetRequestHeader(_apiKeyHeaderName, _apiKey);
        await _ws.ConnectAsync(_endpoint, ct).ConfigureAwait(false);
    }

    public async ValueTask SendEventAsync(string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await SendBytesAsync(bytes, ct).ConfigureAwait(false);
    }

    /// <summary>Send UTF-8 JSON bytes directly — no string round-trip (audio uplink hot path).</summary>
    public ValueTask SendEventAsync(ReadOnlyMemory<byte> utf8Json, CancellationToken ct)
        => SendBytesAsync(utf8Json, ct);

    private async ValueTask SendBytesAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async IAsyncEnumerable<string> ReadEventsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();

        while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await _ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            if (result.MessageType == WebSocketMessageType.Close) yield break;

            // Accumulate raw bytes and decode the COMPLETE message once at EndOfMessage. Decoding each
            // fragment separately (the old per-fragment GetString) corrupts a multi-byte UTF-8 sequence the
            // transport splits across two receives — plausible for large base64 audio payloads (CQ-006).
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                var msg = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);
                yield return msg;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown", default).ConfigureAwait(false);
            }
        }
        catch { /* best-effort close */ }

        _ws.Dispose();
        _sendLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
