using System.Buffers;
using System.Net.WebSockets;
using Voxa.Frames;
using Voxa.Processors;
using Voxa.Transports.WebSocket.Protocol;

namespace Voxa.Transports.WebSocket;

/// <summary>
/// Pipeline source backed by a <see cref="System.Net.WebSockets.WebSocket"/>. Reads inbound binary
/// frames as <see cref="AudioRawFrame"/>s and inbound JSON text frames as control/data frames per
/// <see cref="WireProtocol"/>. Caller owns the WebSocket's lifetime — this processor will not
/// dispose it.
/// </summary>
public sealed class WebSocketAudioSource : PipelineSource
{
    private readonly System.Net.WebSockets.WebSocket _ws;
    private readonly WebSocketAudioOptions _options;
    private Task? _readLoop;

    public WebSocketAudioSource(System.Net.WebSockets.WebSocket webSocket, WebSocketAudioOptions? options = null)
        : base("WebSocketAudioSource")
    {
        _ws = webSocket ?? throw new ArgumentNullException(nameof(webSocket));
        _options = options ?? new WebSocketAudioOptions();
    }

    protected override ValueTask OnStartAsync(StartFrame frame, CancellationToken ct)
    {
        _readLoop = Task.Run(() => ReadLoopAsync(ct), ct);
        return ValueTask.CompletedTask;
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[_options.ReadBufferSize];
        byte[]? acc = null;     // rented; only used for messages that span multiple receives
        int accLen = 0;

        try
        {
            while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (WebSocketException ex)
                {
                    await IngestAsync(
                        new ErrorFrame($"WebSocket receive failed: {ex.Message}", ex) { Direction = FrameDirection.Upstream },
                        ct).ConfigureAwait(false);
                    return;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await IngestAsync(new EndFrame(), ct).ConfigureAwait(false);
                    return;
                }

                ReadOnlyMemory<byte> message;
                if (result.EndOfMessage && accLen == 0)
                {
                    // Fast path — the whole message arrived in one receive. This is the common
                    // case: a 20 ms PCM chunk is ~640 B against a 16 KB read buffer.
                    message = buffer.AsMemory(0, result.Count);
                }
                else
                {
                    // Slow path — accumulate across receives in a pooled buffer.
                    EnsureCapacity(ref acc, accLen + result.Count);
                    buffer.AsSpan(0, result.Count).CopyTo(acc.AsSpan(accLen));
                    accLen += result.Count;
                    if (!result.EndOfMessage) continue;
                    message = acc.AsMemory(0, accLen);
                    accLen = 0;
                }

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    // Single exact-size copy. This array becomes the frame's payload and flows
                    // downstream with unbounded lifetime — it must NOT come from a pool.
                    var pcm = message.ToArray();
                    await IngestAsync(
                        new AudioRawFrame(pcm, _options.InputSampleRate, _options.Channels),
                        ct).ConfigureAwait(false);
                }
                else if (result.MessageType == WebSocketMessageType.Text)
                {
                    var parsed = WireProtocol.TryParseClientMessage(message.Span);
                    if (parsed is not null)
                    {
                        await IngestAsync(parsed, ct).ConfigureAwait(false);
                    }
                }
            }
        }
        finally
        {
            if (acc is not null) ArrayPool<byte>.Shared.Return(acc);
        }

        static void EnsureCapacity(ref byte[]? acc, int needed)
        {
            if (acc is null)
            {
                acc = ArrayPool<byte>.Shared.Rent(Math.Max(needed, 32 * 1024));
            }
            else if (acc.Length < needed)
            {
                var bigger = ArrayPool<byte>.Shared.Rent(Math.Max(needed, acc.Length * 2));
                acc.AsSpan(0, acc.Length).CopyTo(bigger);
                ArrayPool<byte>.Shared.Return(acc);
                acc = bigger;
            }
        }
    }
}
