using System.Text.Json;
using Voxa.Transports.Telephony;

namespace Voxa.Transports.Twilio;

/// <summary>
/// <see cref="ITelephonyMediaCodec"/> for Twilio Media Streams. Parses the inbound
/// <c>connected/start/media/dtmf/mark/stop</c> JSON events and serializes outbound
/// <c>media/clear/mark</c>. The wire audio is base64-encoded G.711 μ-law at 8 kHz mono.
///
/// <para>
/// One instance is shared by the source and the sink for a call (see <see cref="ITelephonyMediaCodec"/>).
/// The <c>start</c> event (parsed on the source's read loop) captures the <see cref="StreamSid"/> needed to
/// address every outbound message and any <see cref="CustomParameters"/> (Twilio's per-call metadata); the
/// sink reads the id when building messages. Both are published via volatile fields. Until the id is
/// captured the <c>Build*</c> methods return <c>null</c> and the sink skips the send (the stream isn't
/// addressable yet — which never happens in practice, since Twilio sends <c>start</c> before any audio).
/// </para>
/// </summary>
public sealed class TwilioMediaStreamCodec : ITelephonyMediaCodec
{
    private volatile string? _streamSid;
    private volatile IReadOnlyDictionary<string, string>? _customParameters;

    /// <summary>μ-law, 8 kHz, mono — the Twilio Media Streams default.</summary>
    public TelephonyMediaFormat WireFormat => TelephonyMediaFormat.MuLaw8k;

    /// <summary>The Twilio stream id captured from the <c>start</c> event (null until the stream starts).</summary>
    public string? StreamSid => _streamSid;

    /// <summary>
    /// The <c>customParameters</c> from the TwiML <c>&lt;Parameter&gt;</c> elements (Twilio's analog of Voxa's
    /// hello envelope), captured from the <c>start</c> event. Null until the stream starts / when none were set.
    /// </summary>
    public IReadOnlyDictionary<string, string>? CustomParameters => _customParameters;

    public TelephonyInbound Parse(ReadOnlySpan<byte> utf8Message)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8Message);
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("event"u8, out var ev))
                return TelephonyInbound.Ignore;

            if (ev.ValueEquals("media"u8))
            {
                // base64 μ-law → a FRESH byte[] (independent of utf8Message, per the codec ownership contract).
                if (root.TryGetProperty("media"u8, out var media) &&
                    media.TryGetProperty("payload"u8, out var payload) &&
                    payload.ValueKind == JsonValueKind.String)
                {
                    return TelephonyInbound.Audio(payload.GetBytesFromBase64());
                }
                return TelephonyInbound.Ignore;
            }

            if (ev.ValueEquals("start"u8))
            {
                CaptureStart(root);
                return TelephonyInbound.Start;
            }

            if (ev.ValueEquals("stop"u8))
                return TelephonyInbound.Stop;

            if (ev.ValueEquals("dtmf"u8))
            {
                if (root.TryGetProperty("dtmf"u8, out var d) &&
                    d.TryGetProperty("digit"u8, out var digit) &&
                    digit.ValueKind == JsonValueKind.String)
                {
                    return TelephonyInbound.FromDtmf(digit.GetString()!);
                }
                return TelephonyInbound.Ignore;
            }

            // "connected" (handshake), "mark" (playout echo), and any unknown event — ignore defensively.
            return TelephonyInbound.Ignore;
        }
        catch (JsonException) { return TelephonyInbound.Ignore; }   // malformed JSON
        catch (FormatException) { return TelephonyInbound.Ignore; } // malformed base64 payload
    }

    public byte[]? BuildMedia(ReadOnlyMemory<byte> wireAudio)
    {
        var sid = _streamSid;
        return sid is null ? null : TwilioJson.Media(sid, wireAudio.Span);
    }

    public byte[]? BuildClear()
    {
        var sid = _streamSid;
        return sid is null ? null : TwilioJson.Clear(sid);
    }

    public byte[]? BuildMark(string name)
    {
        var sid = _streamSid;
        return sid is null ? null : TwilioJson.Mark(sid, name);
    }

    private void CaptureStart(JsonElement root)
    {
        string? sid = null;

        if (root.TryGetProperty("start"u8, out var start) && start.ValueKind == JsonValueKind.Object)
        {
            if (start.TryGetProperty("streamSid"u8, out var s) && s.ValueKind == JsonValueKind.String)
                sid = s.GetString();

            if (start.TryGetProperty("customParameters"u8, out var cp) && cp.ValueKind == JsonValueKind.Object)
            {
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var p in cp.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String)
                        map[p.Name] = p.Value.GetString()!;
                if (map.Count > 0) _customParameters = map;
            }
        }

        // Fall back to the top-level streamSid present on every Twilio message.
        if (sid is null && root.TryGetProperty("streamSid"u8, out var top) && top.ValueKind == JsonValueKind.String)
            sid = top.GetString();

        if (sid is not null) _streamSid = sid;
    }
}
