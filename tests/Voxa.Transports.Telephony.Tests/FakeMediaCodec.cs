using System.Text;
using System.Text.Json;

namespace Voxa.Transports.Telephony.Tests;

/// <summary>
/// A minimal vendor codec for exercising the shared telephony base WITHOUT any real provider (VTL-001 T1.7).
/// Speaks a tiny JSON protocol: inbound <c>{"t":"start|media|stop|dtmf",...}</c>; outbound media/clear/mark.
/// Lets a test drive the source (queue inbound text) and assert the sink's output (sent text), proving the
/// read/write loops, resample bridge, μ-law edge, epoch purge, and lifecycle mapping in isolation.
/// </summary>
internal sealed class FakeMediaCodec : ITelephonyMediaCodec
{
    private volatile bool _addressable;

    public FakeMediaCodec(TelephonyMediaFormat? wireFormat = null, bool addressable = true)
    {
        WireFormat = wireFormat ?? TelephonyMediaFormat.MuLaw8k;
        _addressable = addressable;
    }

    public TelephonyMediaFormat WireFormat { get; }

    /// <summary>The last DTMF digit parsed, for assertions (the base routes it to the source's hook).</summary>
    public string? LastDtmf { get; private set; }

    public TelephonyInbound Parse(ReadOnlySpan<byte> utf8Message)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8Message);
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("t", out var t))
                return TelephonyInbound.Ignore;

            return t.GetString() switch
            {
                "start" => MarkStarted(),
                "stop" => TelephonyInbound.Stop,
                "media" => root.TryGetProperty("b64", out var b64)
                    ? TelephonyInbound.Audio(Convert.FromBase64String(b64.GetString() ?? string.Empty))
                    : TelephonyInbound.Ignore,
                "dtmf" => Dtmf(root),
                _ => TelephonyInbound.Ignore,
            };
        }
        catch (JsonException) { return TelephonyInbound.Ignore; }
        catch (FormatException) { return TelephonyInbound.Ignore; } // bad base64
    }

    private TelephonyInbound MarkStarted()
    {
        _addressable = true;
        return TelephonyInbound.Start;
    }

    private TelephonyInbound Dtmf(JsonElement root)
    {
        var digit = root.TryGetProperty("d", out var d) ? d.GetString() : null;
        LastDtmf = digit;
        return digit is null ? TelephonyInbound.Ignore : TelephonyInbound.FromDtmf(digit);
    }

    public byte[]? BuildMedia(ReadOnlyMemory<byte> wireAudio)
        => _addressable ? Utf8($"{{\"t\":\"media\",\"b64\":\"{Convert.ToBase64String(wireAudio.Span)}\"}}") : null;

    public byte[]? BuildClear()
        => _addressable ? Utf8("{\"t\":\"clear\"}") : null;

    public byte[]? BuildMark(string name)
        => _addressable ? Utf8($"{{\"t\":\"mark\",\"n\":\"{name}\"}}") : null;

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
}
