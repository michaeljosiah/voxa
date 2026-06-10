using System.Text;
using Voxa.Frames;
using Voxa.Transports.WebSocket.Protocol;

namespace Voxa.Transports.WebSocket.Tests;

/// <summary>
/// Locks the on-the-wire JSON format (VPS-001 WS3). The source-generated UTF-8 serializer must
/// produce byte-for-byte the same bytes the old reflection-based codec did, so existing JS clients
/// keep working. Expected strings are hard-coded — never computed with the serializer under test.
/// </summary>
public class WireProtocolCompatibilityTests
{
    private static string Utf8(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    [Fact]
    public void Transcription_AllFields()
        => Assert.Equal(
            "{\"type\":\"transcription\",\"text\":\"hi\",\"isFinal\":true,\"language\":\"en\",\"speakerId\":\"u1\"}",
            Utf8(WireProtocol.BuildTranscription(new TranscriptionFrame("hi", IsFinal: true, Language: "en", SpeakerId: "u1"))));

    [Fact]
    public void Transcription_OmitsNullLanguageAndSpeaker()
        => Assert.Equal(
            "{\"type\":\"transcription\",\"text\":\"hi\",\"isFinal\":false}",
            Utf8(WireProtocol.BuildTranscription(new TranscriptionFrame("hi", IsFinal: false))));

    [Fact]
    public void Text()
        => Assert.Equal(
            "{\"type\":\"text\",\"text\":\"hello world\"}",
            Utf8(WireProtocol.BuildText("hello world")));

    [Fact]
    public void ToolCall()
        => Assert.Equal(
            "{\"type\":\"toolCall\",\"callId\":\"c1\",\"name\":\"get_weather\",\"argumentsJson\":\"{\\u0022city\\u0022:\\u0022Lagos\\u0022}\"}",
            Utf8(WireProtocol.BuildToolCall(new ToolCallRequestFrame("c1", "get_weather", "{\"city\":\"Lagos\"}"))));

    [Fact]
    public void Speaking()
        => Assert.Equal(
            "{\"type\":\"speaking\",\"who\":\"bot\",\"started\":true}",
            Utf8(WireProtocol.BuildSpeaking("bot", started: true)));

    [Fact]
    public void Interruption()
        => Assert.Equal("{\"type\":\"interruption\"}", Utf8(WireProtocol.BuildInterruption()));

    [Fact]
    public void End()
        => Assert.Equal("{\"type\":\"end\"}", Utf8(WireProtocol.BuildEnd()));

    [Fact]
    public void Status()
        => Assert.Equal("{\"type\":\"status\",\"message\":\"working\"}", Utf8(WireProtocol.BuildStatus("working")));

    [Fact]
    public void Error()
        => Assert.Equal("{\"type\":\"error\",\"message\":\"boom\"}", Utf8(WireProtocol.BuildError("boom")));

    [Fact]
    public void FixedEnvelopes_ReturnCachedInstances()
    {
        // BuildInterruption/BuildEnd hand back the same cached array each call (zero per-send cost).
        Assert.Same(WireProtocol.BuildInterruption(), WireProtocol.BuildInterruption());
        Assert.Same(WireProtocol.BuildEnd(), WireProtocol.BuildEnd());
    }
}
