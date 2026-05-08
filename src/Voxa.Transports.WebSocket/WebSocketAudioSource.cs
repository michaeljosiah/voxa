using System.IO;
using System.Net.WebSockets;
using System.Text;
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
        using var binaryAcc = new MemoryStream();
        var textAcc = new StringBuilder();

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

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                binaryAcc.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    var pcm = binaryAcc.ToArray();
                    binaryAcc.SetLength(0);
                    await IngestAsync(
                        new AudioRawFrame(pcm, _options.InputSampleRate, _options.Channels),
                        ct).ConfigureAwait(false);
                }
            }
            else if (result.MessageType == WebSocketMessageType.Text)
            {
                textAcc.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (result.EndOfMessage)
                {
                    var json = textAcc.ToString();
                    textAcc.Clear();
                    var parsed = WireProtocol.TryParseClientMessage(json);
                    if (parsed is not null)
                    {
                        await IngestAsync(parsed, ct).ConfigureAwait(false);
                    }
                }
            }
        }
    }
}
