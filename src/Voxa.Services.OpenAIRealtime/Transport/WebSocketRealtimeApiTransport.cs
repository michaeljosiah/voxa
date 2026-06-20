using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;

namespace Voxa.Services.OpenAIRealtime.Transport;

/// <summary>
/// Default <see cref="IRealtimeApiTransport"/> backed by <see cref="ClientWebSocket"/>. Sends the
/// two headers OpenAI Realtime requires: <c>Authorization: Bearer &lt;key&gt;</c> and
/// <c>OpenAI-Beta: realtime=v1</c>. Appends <c>?model=&lt;id&gt;</c> to the endpoint.
/// </summary>
public sealed class WebSocketRealtimeApiTransport : IRealtimeApiTransport
{
    private readonly Uri _endpoint;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly ClientWebSocket _ws = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public WebSocketRealtimeApiTransport(Uri endpoint, string apiKey, string model)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _model = model ?? throw new ArgumentNullException(nameof(model));
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        _ws.Options.SetRequestHeader("Authorization", $"Bearer {_apiKey}");
        _ws.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");

        var uri = AppendModelQuery(_endpoint, _model);
        await _ws.ConnectAsync(uri, ct).ConfigureAwait(false);
    }

    private static Uri AppendModelQuery(Uri baseUri, string model)
    {
        var ub = new UriBuilder(baseUri);
        var modelParam = $"model={Uri.EscapeDataString(model)}";
        ub.Query = string.IsNullOrEmpty(ub.Query)
            ? modelParam
            : ub.Query.TrimStart('?') + "&" + modelParam;
        return ub.Uri;
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
