using Voxa.Speech.Mistral;

namespace Voxa.Speech.Mistral.Tests;

/// <summary>The pure SSE layer for streaming transcription: data-line extraction and the total, never-throwing parser.</summary>
public class MistralSttStreamTests
{
    [Fact]
    public void TryReadDataLine_Strips_The_Prefix_And_Optional_Space()
    {
        Assert.True(MistralSttStream.TryReadDataLine("data: {\"a\":1}", out var p));
        Assert.Equal("{\"a\":1}", p);

        Assert.True(MistralSttStream.TryReadDataLine("data:{\"a\":1}", out var p2));
        Assert.Equal("{\"a\":1}", p2);
    }

    [Fact]
    public void TryReadDataLine_Ignores_Non_Data_Lines()
    {
        Assert.False(MistralSttStream.TryReadDataLine("event: transcription.text.delta", out _));
        Assert.False(MistralSttStream.TryReadDataLine(": keep-alive comment", out _));
        Assert.False(MistralSttStream.TryReadDataLine("", out _));
    }

    [Fact]
    public void IsDoneSentinel_Matches_Only_The_Literal()
    {
        Assert.True(MistralSttStream.IsDoneSentinel("[DONE]"));
        Assert.False(MistralSttStream.IsDoneSentinel("{\"type\":\"transcription.done\"}"));
    }

    [Fact]
    public void Parses_A_Delta_From_Text_Or_Delta_Field()
    {
        var fromText = MistralSttStream.Parse("{\"type\":\"transcription.text.delta\",\"text\":\"hi\"}");
        Assert.Equal(MistralSttEventKind.Delta, fromText!.Value.Kind);
        Assert.Equal("hi", fromText.Value.Text);

        // OpenAI-style field name is accepted too.
        var fromDelta = MistralSttStream.Parse("{\"type\":\"transcription.text.delta\",\"delta\":\"yo\"}");
        Assert.Equal(MistralSttEventKind.Delta, fromDelta!.Value.Kind);
        Assert.Equal("yo", fromDelta.Value.Text);
    }

    [Theory]
    [InlineData("transcription.done")]
    [InlineData("transcription.text.done")]
    public void Parses_A_Done_With_Text_And_Language(string type)
    {
        var ev = MistralSttStream.Parse($"{{\"type\":\"{type}\",\"text\":\"all done\",\"language\":\"fr\"}}");
        Assert.Equal(MistralSttEventKind.Done, ev!.Value.Kind);
        Assert.Equal("all done", ev.Value.Text);
        Assert.Equal("fr", ev.Value.Language);
    }

    [Fact]
    public void Unknown_Event_Types_Are_Classified_Other_Not_Thrown()
    {
        var ev = MistralSttStream.Parse("{\"type\":\"transcription.session.created\",\"session\":\"abc\"}");
        Assert.Equal(MistralSttEventKind.Other, ev!.Value.Kind);
    }

    [Fact]
    public void Malformed_Or_Non_Object_Payloads_Return_Null()
    {
        Assert.Null(MistralSttStream.Parse("not json"));
        Assert.Null(MistralSttStream.Parse("[1,2,3]"));
        Assert.Null(MistralSttStream.Parse("\"bare string\""));
    }
}
