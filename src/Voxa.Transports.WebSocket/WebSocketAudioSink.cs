using System.Net.WebSockets;
using System.Text;
using Voxa.Frames;
using Voxa.Processors;
using Voxa.Transports.WebSocket.Protocol;

namespace Voxa.Transports.WebSocket;

/// <summary>
/// Pipeline sink that mirrors the downstream frame stream onto a
/// <see cref="System.Net.WebSockets.WebSocket"/>. <see cref="AudioRawFrame"/>s are sent as binary
/// messages; everything else flows out as JSON text per <see cref="WireProtocol"/>. Caller owns
/// the WebSocket's lifetime — this processor will not dispose it.
/// </summary>
public sealed class WebSocketAudioSink : PipelineSink
{
    private readonly System.Net.WebSockets.WebSocket _ws;
    private readonly Func<Frame, string?>? _customSerializer;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>
    /// Construct a sink. The optional <paramref name="customSerializer"/> lets hosts add
    /// JSON envelopes for frame types Voxa doesn't know about (e.g. AONIK <c>ThreadReadyFrame</c>)
    /// without subclassing or copying the sink. Return <c>null</c> for frames the host doesn't
    /// handle and Voxa's built-in serialization runs as the fallback.
    /// </summary>
    /// <param name="webSocket">Open WebSocket. Caller owns lifetime; sink does NOT dispose it.</param>
    /// <param name="customSerializer">
    /// Optional. Called once per outbound frame BEFORE the built-in switch. If it returns a
    /// non-null string, that string is sent as a text frame and the built-in switch is skipped.
    /// Custom serializer output goes through the same private send-lock as the built-in path.
    /// </param>
    public WebSocketAudioSink(
        System.Net.WebSockets.WebSocket webSocket,
        Func<Frame, string?>? customSerializer = null)
        : base("WebSocketAudioSink")
    {
        _ws = webSocket ?? throw new ArgumentNullException(nameof(webSocket));
        _customSerializer = customSerializer;
    }

    protected override async ValueTask ProcessFrameAsync(Frame frame, CancellationToken ct)
    {
        if (frame.Direction == FrameDirection.Upstream)
        {
            await base.ProcessFrameAsync(frame, ct).ConfigureAwait(false);
            return;
        }

        if (_ws.State == WebSocketState.Open)
        {
            try
            {
                await SendFrameAsync(frame, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (WebSocketException) { /* connection died — base call below still drives EndFrameObserved if EndFrame */ }
        }

        await base.ProcessFrameAsync(frame, ct).ConfigureAwait(false);
    }

    private async Task SendFrameAsync(Frame frame, CancellationToken ct)
    {
        // Host-supplied custom serializer wins for any frame it claims. Returning non-null means
        // "I serialize this one"; returning null means "fall through to the built-in switch."
        if (_customSerializer is not null)
        {
            var customJson = _customSerializer(frame);
            if (customJson is not null)
            {
                await SendTextAsync(customJson, ct).ConfigureAwait(false);
                return;
            }
        }

        switch (frame)
        {
            case AudioRawFrame audio:
                await SendBinaryAsync(audio.Pcm, ct).ConfigureAwait(false);
                break;
            case TranscriptionFrame t:
                await SendTextAsync(WireProtocol.BuildTranscription(t), ct).ConfigureAwait(false);
                break;
            case LlmTextChunkFrame chunk:
                await SendTextAsync(WireProtocol.BuildText(chunk.Text), ct).ConfigureAwait(false);
                break;
            case TextFrame txt:
                await SendTextAsync(WireProtocol.BuildText(txt.Text), ct).ConfigureAwait(false);
                break;
            case ToolCallRequestFrame call:
                await SendTextAsync(WireProtocol.BuildToolCall(call), ct).ConfigureAwait(false);
                break;
            case BotStartedSpeakingFrame:
                await SendTextAsync(WireProtocol.BuildSpeaking("bot", started: true), ct).ConfigureAwait(false);
                break;
            case BotStoppedSpeakingFrame:
                await SendTextAsync(WireProtocol.BuildSpeaking("bot", started: false), ct).ConfigureAwait(false);
                break;
            case UserStartedSpeakingFrame:
                await SendTextAsync(WireProtocol.BuildSpeaking("user", started: true), ct).ConfigureAwait(false);
                break;
            case UserStoppedSpeakingFrame:
                await SendTextAsync(WireProtocol.BuildSpeaking("user", started: false), ct).ConfigureAwait(false);
                break;
            case InterruptionFrame:
                await SendTextAsync(WireProtocol.BuildInterruption(), ct).ConfigureAwait(false);
                break;
            case StatusFrame status:
                await SendTextAsync(WireProtocol.BuildStatus(status.Message), ct).ConfigureAwait(false);
                break;
            case ErrorFrame err:
                await SendTextAsync(WireProtocol.BuildError(err.Message), ct).ConfigureAwait(false);
                break;
            case EndFrame:
                await SendTextAsync(WireProtocol.BuildEnd(), ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _ws.SendAsync(data, WebSocketMessageType.Binary, endOfMessage: true, ct).ConfigureAwait(false);
        }
        finally { _sendLock.Release(); }
    }

    private async Task SendTextAsync(string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
        }
        finally { _sendLock.Release(); }
    }
}
