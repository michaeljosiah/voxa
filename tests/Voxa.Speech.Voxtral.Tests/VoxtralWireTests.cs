using System.Text;
using System.Text.Json;

namespace Voxa.Speech.Voxtral.Tests;

/// <summary>The pure wire layer: outbound envelope builders and the total, never-throwing server parser.</summary>
public class VoxtralWireTests
{
    [Fact]
    public void Parses_Delta_As_An_Interim_Fragment()
    {
        Assert.True(VoxtralWire.TryParseServerMessage(
            "{\"type\":\"transcription.delta\",\"delta\":\"the quick brown\"}", out var m));
        Assert.Equal(VoxtralServerEvent.Delta, m.Kind);
        Assert.Equal("the quick brown", m.Text);
    }

    [Fact]
    public void Parses_Done_As_The_Final_Text()
    {
        Assert.True(VoxtralWire.TryParseServerMessage(
            "{\"type\":\"transcription.done\",\"text\":\"the quick brown fox\"}", out var m));
        Assert.Equal(VoxtralServerEvent.Done, m.Kind);
        Assert.Equal("the quick brown fox", m.Text);
    }

    [Theory]
    [InlineData("{\"type\":\"session.created\",\"id\":\"x\"}")] // a real-but-uninteresting event
    [InlineData("{\"type\":\"transcription.unknown\"}")]        // unknown type
    [InlineData("{\"no_type\":true}")]                          // missing type
    [InlineData("not json at all")]                             // malformed
    [InlineData("[1,2,3]")]                                     // not an object
    public void Ignores_Unknown_Or_Malformed_Frames(string json)
        => Assert.False(VoxtralWire.TryParseServerMessage(json, out _));

    [Fact]
    public void Delta_With_No_Payload_Yields_Empty_Text()
    {
        Assert.True(VoxtralWire.TryParseServerMessage("{\"type\":\"transcription.delta\"}", out var m));
        Assert.Equal(VoxtralServerEvent.Delta, m.Kind);
        Assert.Equal(string.Empty, m.Text);
    }

    [Fact]
    public void SessionUpdate_Carries_Type_And_Model()
    {
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(VoxtralWire.SessionUpdate("my-model")));
        Assert.Equal("session.update", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("my-model", doc.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public void AppendAudio_Base64_RoundTrips_The_Pcm()
    {
        var pcm = new byte[] { 0, 1, 2, 250, 255, 7 };
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(VoxtralWire.AppendAudio(pcm)));
        Assert.Equal("input_audio_buffer.append", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(pcm, Convert.FromBase64String(doc.RootElement.GetProperty("audio").GetString()!));
    }

    [Fact]
    public void Commit_Is_Final_True()
    {
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(VoxtralWire.Commit().ToArray()));
        Assert.Equal("input_audio_buffer.commit", doc.RootElement.GetProperty("type").GetString());
        Assert.True(doc.RootElement.GetProperty("final").GetBoolean());
    }
}
