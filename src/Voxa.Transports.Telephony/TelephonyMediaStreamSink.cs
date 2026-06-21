using System.Diagnostics;
using System.Net.WebSockets;
using System.Threading.Channels;
using Voxa.Audio;
using Voxa.Frames;
using Voxa.Processors;

namespace Voxa.Transports.Telephony;

/// <summary>
/// Vendor-neutral telephony <see cref="PipelineSink"/> (VTL-001). Resamples outbound bot
/// <see cref="AudioRawFrame"/>s down to the wire rate, μ-law-encodes them, and serializes them to the active
/// stream via the <see cref="ITelephonyMediaCodec"/>; on barge-in it purges queued bot audio (epoch bump)
/// and emits the vendor flush (Twilio <c>clear</c>) so the caller stops hearing stale audio almost
/// immediately. Non-telephony frames (transcripts, text, status) are dropped — the phone only carries audio.
/// Caller owns the WebSocket's lifetime — this processor will not dispose it.
///
/// <para>
/// Outbound messages flow through a bounded single-writer queue, exactly as <c>WebSocketAudioSink</c>: the
/// frame loop serializes and enqueues (never blocking on network I/O) and one writer task drains to the
/// socket. The barge-in epoch purge drops audio queued before the most recent interruption; control messages
/// (<c>clear</c>) are never purged. On <see cref="EndFrame"/> the channel is completed <b>unconditionally</b>
/// — even when the socket already left Open (caller hang-up) — so the writer can't strand the data loop
/// (the deadlock <c>WebSocketAudioSink</c> documents and guards).
/// </para>
/// </summary>
public sealed class TelephonyMediaStreamSink : PipelineSink
{
    private readonly record struct Outbound(ReadOnlyMemory<byte> Payload, bool Purgeable, int Epoch);

    private readonly System.Net.WebSockets.WebSocket _ws;
    private readonly ITelephonyMediaCodec _codec;
    private readonly LinearResampler _resampler;
    private readonly Channel<Outbound> _outbound = Channel.CreateBounded<Outbound>(
        new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,   // backpressure into the pipeline; never drop silently
            SingleReader = true,
        });

    private int _epoch;        // bumped on barge-in; the writer drops older-epoch (purgeable) audio
    private Task? _writerTask;
    private long _turnT0;      // UserStoppedSpeaking timestamp for the TTFB metric

    /// <param name="webSocket">Open media WebSocket. Caller owns lifetime; the sink does NOT dispose it.</param>
    /// <param name="codec">The vendor wire codec (shared with the matching source).</param>
    /// <param name="outputSampleRate">The pipeline's announced output rate; bot audio is resampled down from it.</param>
    public TelephonyMediaStreamSink(
        System.Net.WebSockets.WebSocket webSocket,
        ITelephonyMediaCodec codec,
        int outputSampleRate)
        : base("TelephonyMediaStreamSink")
    {
        _ws = webSocket ?? throw new ArgumentNullException(nameof(webSocket));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        if (outputSampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(outputSampleRate));
        // Pipeline output rate → 8 kHz wire. Passthrough when the pipeline already runs at 8 kHz.
        _resampler = new LinearResampler(outputSampleRate, codec.WireFormat.SampleRate);
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

        if (frame is EndFrame)
        {
            // Complete the channel unconditionally. When the socket already left Open (caller hang-up —
            // the most common way a call ends) the enqueue above was skipped, so without this the writer
            // task stays parked in ReadAllAsync and the await below deadlocks the data loop — and with it
            // EndFrameObserved / runner completion. No-op on the graceful path.
            _outbound.Writer.TryComplete();
            if (_writerTask is not null)
            {
                try { await _writerTask.ConfigureAwait(false); } catch { /* best-effort drain */ }
            }

            // Only EndFrame reaches the base sink buffer (it drives EndFrameObserved for the runner).
            // Mirroring every data frame into the inherited unbounded output channel would retain all
            // session audio in memory — nothing drains that channel when this sink IS the transport.
            await base.ProcessFrameAsync(frame, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask EnqueueFrameAsync(Frame frame, CancellationToken ct)
    {
        switch (frame)
        {
            // Barge-in arrives on the SYSTEM loop (concurrent with the data loop), so the epoch bump takes
            // effect immediately even while bot audio is queued. We always emit the vendor flush so Twilio
            // drops what it has already buffered for playout — whether the pipeline signals barge-in via an
            // InterruptionFrame or a UserStartedSpeakingFrame (some configs emit only the latter).
            case InterruptionFrame:
            case UserStartedSpeakingFrame:
                Interlocked.Increment(ref _epoch);
                if (_codec.BuildClear() is { } clear)
                    await WriteAsync(clear, purgeable: false, ct).ConfigureAwait(false);
                break;

            case UserStoppedSpeakingFrame:
                Volatile.Write(ref _turnT0, Stopwatch.GetTimestamp());   // start the voice-to-voice clock
                break;

            case AudioRawFrame audio:
                if (BuildMedia(audio) is { } media)
                    await WriteAsync(media, purgeable: true, ct).ConfigureAwait(false);
                break;

            // Everything else (transcripts, text, session info, speaking-state, status, errors) is not part
            // of the telephony wire protocol — drop it. EndFrame completion is handled in ProcessFrameAsync.
            default:
                break;
        }
    }

    /// <summary>Resample bot audio down to the wire rate, encode it, and serialize a vendor media message.</summary>
    private byte[]? BuildMedia(AudioRawFrame audio)
    {
        var pcm = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(audio.Pcm.Span);
        if (pcm.IsEmpty) return null;

        short[] src;
        int count;
        if (_resampler.IsPassthrough)
        {
            src = pcm.ToArray();
            count = src.Length;
        }
        else
        {
            src = new short[_resampler.MaxOutputSamples(pcm.Length)];
            count = _resampler.Process(pcm, src);
        }

        if (count == 0) return null;
        var wire = TelephonyAudio.PcmToWire(src.AsSpan(0, count), _codec.WireFormat.Encoding);
        return _codec.BuildMedia(wire);
    }

    private ValueTask WriteAsync(ReadOnlyMemory<byte> payload, bool purgeable, CancellationToken ct)
    {
        VoxaMetrics.SinkQueueDepth.Record(_outbound.Reader.Count);
        // Use CancellationToken.None for the channel write (not the per-frame token): Channel.WriteAsync
        // checks the token before writing even when there is capacity, so a barge-in firing concurrently
        // could cancel the enqueue and silently drop a clear. Purgeable audio is gate-kept by the epoch
        // check in the writer instead, which is the correct place to drop stale audio.
        _ = ct; // kept for call-site symmetry; not needed for the channel write
        return _outbound.Writer.WriteAsync(new Outbound(payload, purgeable, Volatile.Read(ref _epoch)), CancellationToken.None);
    }

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var msg in _outbound.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                // Barge-in purge: drop bot audio queued before the most recent interruption.
                if (msg.Purgeable && msg.Epoch != Volatile.Read(ref _epoch)) continue;

                if (_ws.State != WebSocketState.Open) continue;

                if (msg.Purgeable)   // first bot audio after end of user speech → TTFB
                {
                    var t0 = Interlocked.Exchange(ref _turnT0, 0);   // read-and-clear, cross-thread safe
                    if (t0 != 0)
                        VoxaMetrics.TurnTtfbMs.Record(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                }

                try
                {
                    // Telephony media/clear/mark are all JSON text messages.
                    await _ws.SendAsync(msg.Payload, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
                }
                catch (WebSocketException) { return; }   // connection died
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (ChannelClosedException) { /* shutdown */ }
    }
}
