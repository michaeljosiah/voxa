namespace Voxa.Transports.Telephony;

/// <summary>Wire sample encoding of a telephony media stream.</summary>
public enum TelephonyAudioEncoding
{
    /// <summary>G.711 μ-law (PCMU) — one byte per sample. Twilio Media Streams.</summary>
    MuLaw,

    /// <summary>Signed 16-bit little-endian PCM — two bytes per sample. Azure ACS.</summary>
    Pcm16,
}

/// <summary>
/// The negotiated wire media format of a telephony stream. The shared source/sink read
/// <see cref="Encoding"/> to decide μ-law vs. raw PCM and <see cref="SampleRate"/> to size the
/// 8 kHz↔pipeline resample bridge (a no-op when it already equals the pipeline's announced rate).
/// </summary>
public readonly record struct TelephonyMediaFormat(TelephonyAudioEncoding Encoding, int SampleRate, int Channels)
{
    /// <summary>μ-law, 8 kHz, mono — the Twilio Media Streams default.</summary>
    public static TelephonyMediaFormat MuLaw8k { get; } = new(TelephonyAudioEncoding.MuLaw, 8000, 1);

    /// <summary>PCM16, 16 kHz, mono — the Azure ACS default.</summary>
    public static TelephonyMediaFormat Pcm16_16k { get; } = new(TelephonyAudioEncoding.Pcm16, 16000, 1);
}

/// <summary>The kind of transport event a parsed inbound message represents.</summary>
public enum TelephonyInboundKind
{
    /// <summary>Handshake / keepalive / unknown — the base does nothing.</summary>
    Ignore,

    /// <summary>Near-end (caller) audio — <see cref="TelephonyInbound.WireAudio"/> holds the raw wire bytes.</summary>
    Audio,

    /// <summary>The media stream started — the codec has captured the stream id / call metadata.</summary>
    Start,

    /// <summary>The media stream stopped — the base ingests an <see cref="Frames.EndFrame"/>.</summary>
    Stop,

    /// <summary>A DTMF keypad digit — logged behind a hook (a first-class DtmfFrame is a follow-up).</summary>
    Dtmf,
}

/// <summary>
/// The result of parsing one inbound telephony message. For <see cref="TelephonyInboundKind.Audio"/>,
/// <see cref="WireAudio"/> carries the raw wire bytes (still at the codec's <see cref="TelephonyMediaFormat.Encoding"/>
/// and <see cref="TelephonyMediaFormat.SampleRate"/>) — the base does the μ-law decode and resample so every codec
/// stays trivial.
/// </summary>
public readonly record struct TelephonyInbound
{
    private TelephonyInbound(TelephonyInboundKind kind, ReadOnlyMemory<byte> wireAudio, string? dtmf)
    {
        Kind = kind;
        WireAudio = wireAudio;
        Dtmf = dtmf;
    }

    /// <summary>What this message is.</summary>
    public TelephonyInboundKind Kind { get; }

    /// <summary>Raw wire audio bytes — valid only when <see cref="Kind"/> is <see cref="TelephonyInboundKind.Audio"/>.</summary>
    public ReadOnlyMemory<byte> WireAudio { get; }

    /// <summary>The DTMF digit(s) — valid only when <see cref="Kind"/> is <see cref="TelephonyInboundKind.Dtmf"/>.</summary>
    public string? Dtmf { get; }

    /// <summary>Handshake / keepalive / unknown event — do nothing.</summary>
    public static readonly TelephonyInbound Ignore = new(TelephonyInboundKind.Ignore, default, null);

    /// <summary>Stream-start event (the codec has captured the stream id / metadata).</summary>
    public static readonly TelephonyInbound Start = new(TelephonyInboundKind.Start, default, null);

    /// <summary>Stream-stop event.</summary>
    public static readonly TelephonyInbound Stop = new(TelephonyInboundKind.Stop, default, null);

    /// <summary>An inbound audio chunk carrying <paramref name="wireAudio"/> (raw wire bytes).</summary>
    public static TelephonyInbound Audio(ReadOnlyMemory<byte> wireAudio)
        => new(TelephonyInboundKind.Audio, wireAudio, null);

    /// <summary>A DTMF digit event.</summary>
    public static TelephonyInbound FromDtmf(string digit)
        => new(TelephonyInboundKind.Dtmf, default, digit);
}

/// <summary>
/// Per-vendor wire codec for a telephony media stream (Twilio Media Streams, Azure ACS, …). The shared
/// <see cref="TelephonyMediaStreamSource"/>/<see cref="TelephonyMediaStreamSink"/> own the WebSocket, the
/// bounded outbound queue + barge-in epoch purge, and the sample-rate bridge; the codec owns ONLY the
/// JSON/base64 framing and the vendor's negotiated media format.
///
/// <para>
/// <b>Lifetime &amp; threading.</b> One codec instance is shared by the source and the sink for a single
/// call. <see cref="Parse"/> is called only from the source's single read loop; the <c>Build*</c> methods
/// only from the sink's single writer task. Any state the codec captures during <see cref="Parse"/> (e.g.
/// the stream id needed to address outbound messages) and reads in a <c>Build*</c> method crosses those two
/// threads, so the implementation must publish it safely (volatile / interlocked). The vendor's
/// stream-start event always precedes any outbound audio, so the id is set before the sink first needs it.
/// </para>
/// </summary>
public interface ITelephonyMediaCodec
{
    /// <summary>
    /// The wire media format (e.g. μ-law 8 kHz mono for Twilio, PCM16 16 kHz for ACS). The base uses
    /// <see cref="TelephonyMediaFormat.SampleRate"/> to size the 8k↔pipeline resample and
    /// <see cref="TelephonyMediaFormat.Encoding"/> to pick μ-law vs. raw PCM.
    /// </summary>
    TelephonyMediaFormat WireFormat { get; }

    /// <summary>
    /// Parse one inbound text message into a single transport event: decoded near-end audio (still at
    /// wire rate/encoding — the base resamples), start/stop lifecycle, dtmf, or "ignore". Implementations
    /// MUST treat unrecognized/malformed input defensively, returning <see cref="TelephonyInbound.Ignore"/>
    /// rather than throwing (an unknown vendor event must not kill the read loop).
    /// <para>
    /// <b>Ownership:</b> for an audio event, <see cref="TelephonyInbound.WireAudio"/> must be memory the codec
    /// owns (e.g. a fresh array from base64-decoding the payload), NOT a slice of <paramref name="utf8Message"/>
    /// — the base reuses the receive buffer across reads and consumes the event after an <c>await</c>.
    /// </para>
    /// </summary>
    TelephonyInbound Parse(ReadOnlySpan<byte> utf8Message);

    /// <summary>
    /// Serialize one chunk of outbound audio (already wire-rate, wire-encoding) into a vendor "media"
    /// message addressed to the active stream. Returns <c>null</c> when the stream is not yet addressable
    /// (no stream id captured) — the sink skips a null payload rather than emitting a malformed message.
    /// </summary>
    byte[]? BuildMedia(ReadOnlyMemory<byte> wireAudio);

    /// <summary>
    /// The vendor's "flush my buffered playout now" message (Twilio: <c>clear</c>). Sent on barge-in so the
    /// caller stops hearing now-stale bot audio almost immediately. Returns <c>null</c> when the vendor has
    /// no flush message or the stream is not yet addressable.
    /// </summary>
    byte[]? BuildClear();

    /// <summary>
    /// Optional playout marker (Twilio: <c>mark</c>) to detect when the caller has heard a chunk. Returns
    /// <c>null</c> when unsupported or the stream is not yet addressable.
    /// </summary>
    byte[]? BuildMark(string name);
}
