using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Voxa.Frames;
using Voxa.Processors;
using Voxa.Transports.WebSocket.Protocol;

namespace Voxa.Transports.WebSocket;

/// <summary>
/// Pipeline sink that mirrors the downstream frame stream onto a
/// <see cref="System.Net.WebSockets.WebSocket"/>. <see cref="AudioRawFrame"/>s are sent as binary
/// messages; everything else flows out as JSON text per <see cref="WireProtocol"/>. Caller owns
/// the WebSocket's lifetime — this processor will not dispose it.
///
/// <para>
/// Outbound messages flow through a bounded single-writer queue: the data loop serializes and
/// enqueues (never blocking on network I/O), and one writer task drains to the socket. On
/// <see cref="InterruptionFrame"/> the epoch is bumped and queued audio from the previous epoch
/// is dropped, so the bot's audio stops almost immediately on barge-in. Non-audio messages
/// (transcriptions, tool calls, status) are never purged.
/// </para>
/// </summary>
public sealed class WebSocketAudioSink : PipelineSink
{
    private readonly record struct Outbound(ReadOnlyMemory<byte> Payload, bool IsBinaryAudio, int Epoch);

    private readonly System.Net.WebSockets.WebSocket _ws;
    private readonly Func<Frame, string?>? _customSerializer;
    private readonly Channel<Outbound> _outbound = Channel.CreateBounded<Outbound>(
        new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,   // backpressure into the pipeline; never drop silently
            SingleReader = true,
        });

    private int _epoch;        // bumped on interruption; the writer drops older-epoch audio
    private Task? _writerTask;
    private long _turnT0;      // WS0.3: UserStoppedSpeaking timestamp for the TTFB metric

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
    /// Custom serializer output goes through the same outbound queue as the built-in path.
    /// </param>
    public WebSocketAudioSink(
        System.Net.WebSockets.WebSocket webSocket,
        Func<Frame, string?>? customSerializer = null)
        : base("WebSocketAudioSink")
    {
        _ws = webSocket ?? throw new ArgumentNullException(nameof(webSocket));
        _customSerializer = customSerializer;
    }

    protected override ValueTask OnStartAsync(StartFrame frame, CancellationToken ct)
    {
        _writerTask = Task.Run(() => WriteLoopAsync(ct), ct);
        return ValueTask.CompletedTask;
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
                await EnqueueFrameAsync(frame, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (ChannelClosedException) { /* writer already torn down with the socket */ }
        }

        // Graceful end: flush the queue (incl. the "end" envelope) before the runner observes end.
        if (frame is EndFrame && _writerTask is not null)
        {
            try { await _writerTask.ConfigureAwait(false); } catch { /* best-effort drain */ }
        }

        await base.ProcessFrameAsync(frame, ct).ConfigureAwait(false);
    }

    private async ValueTask EnqueueFrameAsync(Frame frame, CancellationToken ct)
    {
        // Interruption arrives on the SYSTEM loop (concurrent with the data loop), so the epoch
        // bump takes effect immediately even while audio is queued. The envelope itself jumps
        // ahead of the now-stale audio so the client stops local playback.
        if (frame is InterruptionFrame)
        {
            Interlocked.Increment(ref _epoch);
            await WriteAsync(WireProtocol.BuildInterruption(), isBinaryAudio: false, ct).ConfigureAwait(false);
            return;
        }

        // A UserStartedSpeaking arriving as a data frame is also a barge-in: bump the epoch so
        // pipelines without an explicit InterruptionFrame still purge stale audio.
        if (frame is UserStartedSpeakingFrame)
        {
            Interlocked.Increment(ref _epoch);
        }
        else if (frame is UserStoppedSpeakingFrame)
        {
            _turnT0 = Stopwatch.GetTimestamp();   // WS0.3: start the voice-to-voice clock
        }

        if (_customSerializer is not null)
        {
            var customJson = _customSerializer(frame);
            if (customJson is not null)
            {
                await WriteAsync(Encoding.UTF8.GetBytes(customJson), isBinaryAudio: false, ct).ConfigureAwait(false);
                return;
            }
        }

        switch (frame)
        {
            case AudioRawFrame audio:
                await WriteAsync(audio.Pcm, isBinaryAudio: true, ct).ConfigureAwait(false);
                break;
            case TranscriptionFrame t:
                await WriteAsync(WireProtocol.BuildTranscription(t), isBinaryAudio: false, ct).ConfigureAwait(false);
                break;
            case LlmTextChunkFrame chunk:
                await WriteAsync(WireProtocol.BuildText(chunk.Text), isBinaryAudio: false, ct).ConfigureAwait(false);
                break;
            case TextFrame txt:
                await WriteAsync(WireProtocol.BuildText(txt.Text), isBinaryAudio: false, ct).ConfigureAwait(false);
                break;
            case ToolCallRequestFrame call:
                await WriteAsync(WireProtocol.BuildToolCall(call), isBinaryAudio: false, ct).ConfigureAwait(false);
                break;
            case BotStartedSpeakingFrame:
                await WriteAsync(WireProtocol.BuildSpeaking("bot", started: true), isBinaryAudio: false, ct).ConfigureAwait(false);
                break;
            case BotStoppedSpeakingFrame:
                await WriteAsync(WireProtocol.BuildSpeaking("bot", started: false), isBinaryAudio: false, ct).ConfigureAwait(false);
                break;
            case UserStartedSpeakingFrame:
                await WriteAsync(WireProtocol.BuildSpeaking("user", started: true), isBinaryAudio: false, ct).ConfigureAwait(false);
                break;
            case UserStoppedSpeakingFrame:
                await WriteAsync(WireProtocol.BuildSpeaking("user", started: false), isBinaryAudio: false, ct).ConfigureAwait(false);
                break;
            case StatusFrame status:
                await WriteAsync(WireProtocol.BuildStatus(status.Message), isBinaryAudio: false, ct).ConfigureAwait(false);
                break;
            case ErrorFrame err:
                await WriteAsync(WireProtocol.BuildError(err.Message), isBinaryAudio: false, ct).ConfigureAwait(false);
                break;
            case EndFrame:
                await WriteAsync(WireProtocol.BuildEnd(), isBinaryAudio: false, ct).ConfigureAwait(false);
                _outbound.Writer.TryComplete();   // drain remaining items, then the writer exits
                break;
        }
    }

    private ValueTask WriteAsync(ReadOnlyMemory<byte> payload, bool isBinaryAudio, CancellationToken ct)
    {
        VoxaMetrics.SinkQueueDepth.Record(_outbound.Reader.Count);
        return _outbound.Writer.WriteAsync(new Outbound(payload, isBinaryAudio, Volatile.Read(ref _epoch)), ct);
    }

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var msg in _outbound.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                // Barge-in purge: drop audio queued before the most recent interruption.
                if (msg.IsBinaryAudio && msg.Epoch != Volatile.Read(ref _epoch)) continue;

                if (_ws.State != WebSocketState.Open) continue;

                if (msg.IsBinaryAudio && _turnT0 != 0)   // WS0.3: first bot audio after end of user speech
                {
                    VoxaMetrics.TurnTtfbMs.Record(Stopwatch.GetElapsedTime(_turnT0).TotalMilliseconds);
                    _turnT0 = 0;
                }

                try
                {
                    await _ws.SendAsync(
                        msg.Payload,
                        msg.IsBinaryAudio ? WebSocketMessageType.Binary : WebSocketMessageType.Text,
                        endOfMessage: true, ct).ConfigureAwait(false);
                }
                catch (WebSocketException) { return; }   // connection died
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (ChannelClosedException) { /* shutdown */ }
    }
}
