using System.Buffers;
using System.Net.WebSockets;
using Voxa.Audio;
using Voxa.Frames;
using Voxa.Processors;

namespace Voxa.Transports.Telephony;

/// <summary>
/// Vendor-neutral telephony <see cref="PipelineSource"/> (VTL-001). Reads the inbound media WebSocket,
/// hands each text message to the <see cref="ITelephonyMediaCodec"/>, and turns decoded near-end audio into
/// <see cref="AudioRawFrame"/>s at the pipeline's announced input rate (μ-law decode + 8 kHz→Nk resample at
/// the edge). Maps the vendor's stop event (and a socket close / caller hang-up) to an <see cref="EndFrame"/>.
/// Caller owns the WebSocket's lifetime — this processor will not dispose it. Models
/// <c>WebSocketAudioSource</c>, with the codec spliced into the (text-only) audio path.
/// </summary>
public sealed class TelephonyMediaStreamSource : PipelineSource
{
    private const int ReadBufferSize = 16 * 1024;

    private readonly System.Net.WebSockets.WebSocket _ws;
    private readonly ITelephonyMediaCodec _codec;
    private readonly int _inputSampleRate;
    private readonly Action<string>? _onDtmf;
    private readonly LinearResampler _resampler;
    private Task? _readLoop;

    /// <param name="webSocket">Open media WebSocket. Caller owns lifetime; the source does NOT dispose it.</param>
    /// <param name="codec">The vendor wire codec (shared with the matching sink).</param>
    /// <param name="inputSampleRate">The pipeline's announced input rate; inbound audio is resampled to it.</param>
    /// <param name="onDtmf">Optional hook for DTMF digits (a first-class DtmfFrame is a follow-up). Null ⇒ ignored.</param>
    public TelephonyMediaStreamSource(
        System.Net.WebSockets.WebSocket webSocket,
        ITelephonyMediaCodec codec,
        int inputSampleRate,
        Action<string>? onDtmf = null)
        : base("TelephonyMediaStreamSource")
    {
        _ws = webSocket ?? throw new ArgumentNullException(nameof(webSocket));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        if (inputSampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(inputSampleRate));
        _inputSampleRate = inputSampleRate;
        _onDtmf = onDtmf;
        // 8 kHz wire → pipeline input rate. Passthrough when a "telephony" STT announces 8 kHz.
        _resampler = new LinearResampler(_codec.WireFormat.SampleRate, inputSampleRate);
    }

    protected override ValueTask OnStartAsync(StartFrame frame, CancellationToken ct)
    {
        _readLoop = Task.Run(() => ReadLoopAsync(ct), ct);
        return ValueTask.CompletedTask;
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[ReadBufferSize];
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
                        new ErrorFrame($"Telephony WebSocket receive failed: {ex.Message}", ex) { Direction = FrameDirection.Upstream },
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
                    // Fast path — the whole message arrived in one receive (a 20 ms μ-law chunk is
                    // ~220 B of base64 + envelope, well under the read buffer).
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

                // Telephony media arrives as JSON text; binary frames aren't part of the protocol — ignore.
                if (result.MessageType != WebSocketMessageType.Text) continue;

                // Parse synchronously here (a span operation) — the codec returns owned memory, so the
                // resulting event survives the awaits below even though `message` aliases a reused buffer.
                var inbound = _codec.Parse(message.Span);
                if (await HandleInboundAsync(inbound, ct).ConfigureAwait(false))
                    return; // stop event — EndFrame already ingested
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

    /// <summary>Act on one parsed inbound event. Returns true when the stream has stopped (read loop exits).</summary>
    private async ValueTask<bool> HandleInboundAsync(TelephonyInbound inbound, CancellationToken ct)
    {
        switch (inbound.Kind)
        {
            case TelephonyInboundKind.Audio:
            {
                var frame = DecodeToFrame(inbound.WireAudio.Span);
                if (frame is not null)
                    await IngestAsync(frame, ct).ConfigureAwait(false);
                return false;
            }
            case TelephonyInboundKind.Stop:
                await IngestAsync(new EndFrame(), ct).ConfigureAwait(false);
                return true;
            case TelephonyInboundKind.Dtmf:
                if (inbound.Dtmf is { } digit) _onDtmf?.Invoke(digit);
                return false;
            case TelephonyInboundKind.Start:
            case TelephonyInboundKind.Ignore:
            default:
                return false;
        }
    }

    /// <summary>
    /// Decode one wire audio chunk to an <see cref="AudioRawFrame"/> at the announced input rate:
    /// μ-law → PCM16 @ 8 kHz → PCM16 @ Nk. The payload is an exact-size array — it becomes the frame's
    /// payload and flows downstream with unbounded lifetime, so it must NOT come from a pool.
    /// </summary>
    private AudioRawFrame? DecodeToFrame(ReadOnlySpan<byte> wireAudio)
    {
        if (wireAudio.IsEmpty) return null;

        var wirePcm = TelephonyAudio.WireToPcm(wireAudio, _codec.WireFormat.Encoding);

        short[] src;
        int count;
        if (_resampler.IsPassthrough)
        {
            src = wirePcm;
            count = wirePcm.Length;
        }
        else
        {
            src = new short[_resampler.MaxOutputSamples(wirePcm.Length)];
            count = _resampler.Process(wirePcm, src);
        }

        if (count == 0) return null;
        var payload = TelephonyAudio.PcmToBytes(src.AsSpan(0, count));
        return new AudioRawFrame(payload, _inputSampleRate, 1);
    }
}
