using System.Text.Json;
using Voxa.Frames;
using Voxa.Transports.WebSocket.Protocol;

namespace Voxa.Transports.WebSocket.Tests;

public class WireProtocolTests
{
    [Fact]
    public void End_Message_Parses_To_EndFrame()
    {
        var frame = WireProtocol.TryParseClientMessage("{\"type\":\"end\"}");
        Assert.IsType<EndFrame>(frame);
    }

    [Fact]
    public void Hello_Message_Returns_Null_So_Caller_Can_Handle_Out_Of_Band()
    {
        var frame = WireProtocol.TryParseClientMessage("{\"type\":\"hello\",\"sampleRate\":24000}");
        Assert.Null(frame);
    }

    [Fact]
    public void Text_Message_Parses_To_TextFrame()
    {
        var frame = WireProtocol.TryParseClientMessage("{\"type\":\"text\",\"text\":\"hi there\"}");
        var t = Assert.IsType<TextFrame>(frame);
        Assert.Equal("hi there", t.Text);
    }

    [Fact]
    public void ToolResult_Message_Parses_To_ToolCallResultFrame()
    {
        var json = "{\"type\":\"toolResult\",\"callId\":\"c1\",\"resultJson\":\"{\\\"ok\\\":true}\",\"isError\":false}";
        var frame = WireProtocol.TryParseClientMessage(json);
        var t = Assert.IsType<ToolCallResultFrame>(frame);
        Assert.Equal("c1", t.CallId);
        Assert.Contains("ok", t.ResultJson);
        Assert.False(t.IsError);
    }

    [Fact]
    public void Unknown_Type_Returns_Null()
    {
        Assert.Null(WireProtocol.TryParseClientMessage("{\"type\":\"nope\"}"));
    }

    [Fact]
    public void Malformed_Json_Returns_Null_Not_Throw()
    {
        Assert.Null(WireProtocol.TryParseClientMessage("not json {"));
    }

    [Fact]
    public void Builds_Transcription_With_All_Fields()
    {
        var json = WireProtocol.BuildTranscription(new TranscriptionFrame("hello", IsFinal: true, Language: "en", SpeakerId: "user1"));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("transcription", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("hello", doc.RootElement.GetProperty("text").GetString());
        Assert.True(doc.RootElement.GetProperty("isFinal").GetBoolean());
        Assert.Equal("en", doc.RootElement.GetProperty("language").GetString());
        Assert.Equal("user1", doc.RootElement.GetProperty("speakerId").GetString());
    }

    [Fact]
    public void Builds_ToolCall_With_All_Fields()
    {
        var json = WireProtocol.BuildToolCall(new ToolCallRequestFrame("c1", "get_weather", "{\"city\":\"Lagos\"}"));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("toolCall", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("c1", doc.RootElement.GetProperty("callId").GetString());
        Assert.Equal("get_weather", doc.RootElement.GetProperty("name").GetString());
        Assert.Contains("Lagos", doc.RootElement.GetProperty("argumentsJson").GetString());
    }

    [Fact]
    public void Builds_Speaking_Bot_Started()
    {
        var json = WireProtocol.BuildSpeaking("bot", started: true);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("speaking", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("bot", doc.RootElement.GetProperty("who").GetString());
        Assert.True(doc.RootElement.GetProperty("started").GetBoolean());
    }

    [Fact]
    public void Builds_Error()
    {
        var json = WireProtocol.BuildError("kaboom");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("error", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("kaboom", doc.RootElement.GetProperty("message").GetString());
    }
}
