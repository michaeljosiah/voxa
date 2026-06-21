using System.Text;
using System.Text.Json;
using Voxa.Transports.Telephony;
using Voxa.Transports.Twilio;

namespace Voxa.Transports.Twilio.Tests;

/// <summary>
/// Parser/serializer behavior of <see cref="TwilioMediaStreamCodec"/> against real-shaped Twilio Media
/// Streams envelopes (VTL-001 T2.1/T2.2/T2.5). Offline — no account, no network.
/// </summary>
public class TwilioMediaStreamCodecTests
{
    private const string Sid = "MZ1234567890abcdef";

    private static ReadOnlySpan<byte> Utf8(string s) => Encoding.UTF8.GetBytes(s);

    private static string StartEnvelope(string streamSid = Sid, bool customParams = true, bool nestSid = true)
    {
        var sidInStart = nestSid ? $"\"streamSid\":\"{streamSid}\"," : "";
        var custom = customParams ? ",\"customParameters\":{\"plan\":\"pro\",\"lang\":\"en\"}" : "";
        return $$"""
            {"event":"start","sequenceNumber":"1","start":{{"{"}}{{sidInStart}}"accountSid":"AC1","callSid":"CA1","tracks":["inbound"],"mediaFormat":{"encoding":"audio/x-mulaw","sampleRate":8000,"channels":1}{{custom}}},"streamSid":"{{streamSid}}"}
            """;
    }

    private static string MediaEnvelope(byte[] muLaw)
        => $"{{\"event\":\"media\",\"sequenceNumber\":\"2\",\"media\":{{\"track\":\"inbound\",\"chunk\":\"1\",\"timestamp\":\"5\",\"payload\":\"{Convert.ToBase64String(muLaw)}\"}},\"streamSid\":\"{Sid}\"}}";

    [Fact]
    public void WireFormat_Is_MuLaw_8k_Mono()
    {
        var codec = new TwilioMediaStreamCodec();
        Assert.Equal(TelephonyAudioEncoding.MuLaw, codec.WireFormat.Encoding);
        Assert.Equal(8000, codec.WireFormat.SampleRate);
        Assert.Equal(1, codec.WireFormat.Channels);
    }

    [Fact]
    public void Parse_Connected_Is_Ignored()
    {
        var codec = new TwilioMediaStreamCodec();
        var inbound = codec.Parse(Utf8("{\"event\":\"connected\",\"protocol\":\"Call\",\"version\":\"1.0.0\"}"));
        Assert.Equal(TelephonyInboundKind.Ignore, inbound.Kind);
    }

    [Fact]
    public void Parse_Start_Captures_StreamSid_And_CustomParameters()
    {
        var codec = new TwilioMediaStreamCodec();
        var inbound = codec.Parse(Utf8(StartEnvelope()));

        Assert.Equal(TelephonyInboundKind.Start, inbound.Kind);
        Assert.Equal(Sid, codec.StreamSid);
        Assert.NotNull(codec.CustomParameters);
        Assert.Equal("pro", codec.CustomParameters!["plan"]);
        Assert.Equal("en", codec.CustomParameters!["lang"]);
    }

    [Fact]
    public void Parse_Start_Falls_Back_To_TopLevel_StreamSid()
    {
        var codec = new TwilioMediaStreamCodec();
        codec.Parse(Utf8(StartEnvelope(nestSid: false)));   // streamSid only at the top level
        Assert.Equal(Sid, codec.StreamSid);
    }

    [Fact]
    public void Parse_Media_Decodes_Base64_MuLaw_Payload()
    {
        var muLaw = new byte[] { 0x00, 0x7F, 0x80, 0xFF, 0x12, 0xAB };
        var codec = new TwilioMediaStreamCodec();

        var inbound = codec.Parse(Utf8(MediaEnvelope(muLaw)));

        Assert.Equal(TelephonyInboundKind.Audio, inbound.Kind);
        Assert.Equal(muLaw, inbound.WireAudio.ToArray());
    }

    [Fact]
    public void Parse_Dtmf_Returns_Digit()
    {
        var codec = new TwilioMediaStreamCodec();
        var inbound = codec.Parse(Utf8($"{{\"event\":\"dtmf\",\"dtmf\":{{\"track\":\"inbound_track\",\"digit\":\"7\"}},\"streamSid\":\"{Sid}\"}}"));
        Assert.Equal(TelephonyInboundKind.Dtmf, inbound.Kind);
        Assert.Equal("7", inbound.Dtmf);
    }

    [Fact]
    public void Parse_Mark_Is_Ignored()
    {
        var codec = new TwilioMediaStreamCodec();
        var inbound = codec.Parse(Utf8($"{{\"event\":\"mark\",\"mark\":{{\"name\":\"utt-7-end\"}},\"streamSid\":\"{Sid}\"}}"));
        Assert.Equal(TelephonyInboundKind.Ignore, inbound.Kind);
    }

    [Fact]
    public void Parse_Stop_Is_Stop()
    {
        var codec = new TwilioMediaStreamCodec();
        var inbound = codec.Parse(Utf8($"{{\"event\":\"stop\",\"stop\":{{\"accountSid\":\"AC1\",\"callSid\":\"CA1\"}},\"streamSid\":\"{Sid}\"}}"));
        Assert.Equal(TelephonyInboundKind.Stop, inbound.Kind);
    }

    [Theory]
    [InlineData("{\"event\":\"galaxy\"}")]        // unknown event
    [InlineData("{\"no\":\"event\"}")]            // no event field
    [InlineData("not json at all")]               // malformed JSON
    [InlineData("{\"event\":\"media\",\"media\":{\"payload\":\"@@not-base64@@\"}}")] // bad base64
    [InlineData("[]")]                            // not an object
    public void Parse_Defensive_Inputs_Are_Ignored(string json)
    {
        var codec = new TwilioMediaStreamCodec();
        Assert.Equal(TelephonyInboundKind.Ignore, codec.Parse(Utf8(json)).Kind);
    }

    // ---- Outbound builders ----

    [Fact]
    public void Build_Methods_Return_Null_Before_Start()
    {
        var codec = new TwilioMediaStreamCodec();   // no streamSid captured yet
        Assert.Null(codec.BuildMedia(new byte[] { 1, 2, 3 }));
        Assert.Null(codec.BuildClear());
        Assert.Null(codec.BuildMark("x"));
    }

    [Fact]
    public void BuildMedia_Emits_Media_Addressed_To_StreamSid()
    {
        var codec = new TwilioMediaStreamCodec();
        codec.Parse(Utf8(StartEnvelope()));
        var muLaw = new byte[] { 0x80, 0x80, 0xFF };

        using var doc = JsonDocument.Parse(codec.BuildMedia(muLaw)!);
        var root = doc.RootElement;
        Assert.Equal("media", root.GetProperty("event").GetString());
        Assert.Equal(Sid, root.GetProperty("streamSid").GetString());
        var payload = root.GetProperty("media").GetProperty("payload").GetString();
        Assert.Equal(muLaw, Convert.FromBase64String(payload!));
    }

    [Fact]
    public void BuildClear_Emits_Clear_Addressed_To_StreamSid()
    {
        var codec = new TwilioMediaStreamCodec();
        codec.Parse(Utf8(StartEnvelope()));

        using var doc = JsonDocument.Parse(codec.BuildClear()!);
        Assert.Equal("clear", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal(Sid, doc.RootElement.GetProperty("streamSid").GetString());
    }

    [Fact]
    public void BuildMark_Emits_Mark_With_Name()
    {
        var codec = new TwilioMediaStreamCodec();
        codec.Parse(Utf8(StartEnvelope()));

        using var doc = JsonDocument.Parse(codec.BuildMark("utt-7-end")!);
        var root = doc.RootElement;
        Assert.Equal("mark", root.GetProperty("event").GetString());
        Assert.Equal(Sid, root.GetProperty("streamSid").GetString());
        Assert.Equal("utt-7-end", root.GetProperty("mark").GetProperty("name").GetString());
    }
}
