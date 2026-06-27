using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Voxa.Speech.Voxtral.Tests;

/// <summary>
/// A minimal in-process fake of vLLM's <c>/v1/realtime</c> WebSocket endpoint for engine tests. Portable by
/// design: a <see cref="TcpListener"/> + a hand-rolled HTTP 101 upgrade, then
/// <see cref="WebSocket.CreateFromStream(Stream, bool, string?, TimeSpan)"/> so the framework owns the RFC-6455
/// framing — unlike <c>HttpListener</c> WebSockets, this works on the Linux default test lane too. It records
/// the handshake model and the decoded audio it received, ignores the path, and on the <c>final:true</c> commit
/// replays the scripted deltas followed by a <c>done</c>.
///
/// <para>Models vLLM's <b>one-shot-per-connection</b> realtime protocol: it accepts connections in a loop (the
/// engine opens one per utterance), each connection finalizes once on <c>commit {final:true}</c>, and a bare
/// commit (the "ready" signal) is a no-op. Per-connection state is local so back-to-back utterances are independent.</para>
/// </summary>
internal sealed class MiniRealtimeServer : IAsyncDisposable
{
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly TcpListener _listener;
    private readonly string[] _deltas;
    private readonly string _doneText;
    private readonly bool _sendDone;
    private readonly int _doneDelayMs;
    private readonly string? _error;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly MemoryStream _audio = new();
    private readonly object _gate = new();

    /// <param name="sendDone">When false, <c>commit</c> replays the deltas but never sends <c>transcription.done</c>
    /// — simulates a dropped/never-finalized utterance so the engine's per-utterance reset can be exercised.</param>
    /// <param name="doneDelayMs">Delay before the <c>transcription.done</c> is sent, so a test can make the final
    /// land while the engine is mid-<c>StopAsync</c> (exercising the stop-drain).</param>
    /// <param name="error">When set, the server sends a <c>{"type":"error",…}</c> frame right after the greeting
    /// (mimics a rejected session, e.g. a bad model name) so the engine's error handling can be exercised.</param>
    public MiniRealtimeServer(string[] deltas, string doneText, bool sendDone = true, int doneDelayMs = 0, string? error = null)
    {
        _deltas = deltas;
        _doneText = doneText;
        _sendDone = sendDone;
        _doneDelayMs = doneDelayMs;
        _error = error;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(() => AcceptAsync(_cts.Token));
    }

    public int Port { get; }

    /// <summary>Base ws URL (no path) for <see cref="VoxtralOptions.ServerUrl"/>; the engine appends /v1/realtime.</summary>
    public string ServerUrl => $"ws://127.0.0.1:{Port}";

    public string? Model { get; private set; }
    public string? Language { get; private set; }
    public int? Delay { get; private set; }

    public byte[] ReceivedAudio { get { lock (_gate) return _audio.ToArray(); } }

    private async Task AcceptAsync(CancellationToken ct)
    {
        // Accept one connection per utterance (the engine reconnects each time), in a loop until shutdown.
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                await HandleConnectionAsync(client, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }       // shutdown
            catch (IOException) { /* connection torn down — accept the next */ }
            catch (SocketException) { break; }                  // listener stopped
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        var stream = client.GetStream();
        await UpgradeAsync(stream, ct).ConfigureAwait(false);
        using var ws = WebSocket.CreateFromStream(
            stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(30));

        // Greet with a frame the engine must ignore (proves unknown types don't break the loop).
        await SendAsync(ws, "{\"type\":\"session.created\",\"id\":\"fake\"}", ct).ConfigureAwait(false);

        // Optionally reject the session right away (e.g. a bad model name) so error handling is exercisable.
        if (_error is not null)
            await SendAsync(ws, $"{{\"type\":\"error\",\"error\":{{\"message\":{Quote(_error)}}}}}", ct).ConfigureAwait(false);

        var audioSinceCommit = false; // per connection: nothing to finalize on an empty buffer (e.g. the ready commit)
        var streamEnded = false;      // a {"final":true} commit ends this connection's stream
        var buffer = new byte[64 * 1024];
        using var msg = new MemoryStream();
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested && !streamEnded)
        {
            WebSocketReceiveResult result;
            try { result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (WebSocketException) { break; }

            if (result.MessageType == WebSocketMessageType.Close) break;
            msg.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;

            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(msg.GetBuffer(), 0, (int)msg.Length));
            msg.SetLength(0);
            if (!doc.RootElement.TryGetProperty("type", out var typeEl)) continue;
            switch (typeEl.GetString())
            {
                case "session.update":
                    Model = doc.RootElement.TryGetProperty("model", out var m) ? m.GetString() : null;
                    Language = doc.RootElement.TryGetProperty("language", out var lang) ? lang.GetString() : null;
                    Delay = doc.RootElement.TryGetProperty("delay", out var d2) && d2.ValueKind == JsonValueKind.Number ? d2.GetInt32() : null;
                    break;
                case "input_audio_buffer.append":
                    if (doc.RootElement.TryGetProperty("audio", out var a) && a.GetString() is { } b64)
                    {
                        lock (_gate) _audio.Write(Convert.FromBase64String(b64));
                        audioSinceCommit = true;
                    }
                    break;
                case "input_audio_buffer.commit":
                    var final = doc.RootElement.TryGetProperty("final", out var f) && f.ValueKind == JsonValueKind.True;
                    if (audioSinceCommit) // a bare/ready commit on an empty buffer is a no-op
                    {
                        foreach (var d in _deltas)
                            await SendAsync(ws, $"{{\"type\":\"transcription.delta\",\"delta\":{Quote(d)}}}", ct).ConfigureAwait(false);
                        if (_sendDone)
                        {
                            if (_doneDelayMs > 0) await Task.Delay(_doneDelayMs, ct).ConfigureAwait(false);
                            await SendAsync(ws, $"{{\"type\":\"transcription.done\",\"text\":{Quote(_doneText)}}}", ct).ConfigureAwait(false);
                        }
                        audioSinceCommit = false;
                    }
                    if (final) streamEnded = true; // one-shot: the connection is finished after final:true
                    break;
            }
        }
    }

    private static string Quote(string s) => JsonSerializer.Serialize(s);

    private static Task SendAsync(WebSocket ws, string text, CancellationToken ct)
        => ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, ct);

    private static async Task UpgradeAsync(NetworkStream stream, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (!sb.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            var n = await stream.ReadAsync(one, ct).ConfigureAwait(false);
            if (n == 0) throw new IOException("client closed during the WebSocket handshake");
            sb.Append((char)one[0]);
        }

        var key = sb.ToString().Split("\r\n")
            .First(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
            .Split(':', 2)[1].Trim();
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));

        var response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n");
        await stream.WriteAsync(response, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        try { await _acceptLoop.ConfigureAwait(false); } catch { /* shutdown */ }
        _cts.Dispose();
        _audio.Dispose();
    }
}
