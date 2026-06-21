using Microsoft.Extensions.Configuration;
using Voxa.Speech.Speechmatics;

namespace Voxa.Speech.Speechmatics.Tests;

/// <summary>WebSocket + handshake plumbing is the shared base's; the Speechmatics seam is the message parser.</summary>
public class SpeechmaticsSttEngineTests
{
    private static IConfigurationSection Root(params (string Key, string Value)[] kv)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(kv.ToDictionary(p => p.Key, p => (string?)p.Value))
            .Build()
            .GetSection("Voxa");

    [Fact]
    public void Parse_partial_is_interim()
    {
        var f = Assert.Single(SpeechmaticsSttEngine.Parse(
            """{"message":"AddPartialTranscript","metadata":{"transcript":"good mor"}}""").ToList());
        Assert.Equal("good mor", f.Text);
        Assert.False(f.IsSegmentFinal);
    }

    [Fact]
    public void Parse_add_transcript_is_locked()
    {
        var f = Assert.Single(SpeechmaticsSttEngine.Parse(
            """{"message":"AddTranscript","metadata":{"transcript":"good morning"}}""").ToList());
        Assert.Equal("good morning", f.Text);
        Assert.True(f.IsSegmentFinal);
    }

    [Theory]
    [InlineData("""{"message":"RecognitionStarted","id":"x"}""")]
    [InlineData("""{"message":"AudioAdded","seq_no":1}""")]
    [InlineData("""{"message":"AddTranscript","metadata":{"transcript":""}}""")] // empty
    public void Parse_ignores_non_transcript_messages(string json)
        => Assert.Empty(SpeechmaticsSttEngine.Parse(json));

    [Fact]
    public void Descriptor_advertises_speechmatics_and_validates_key()
    {
        Assert.Equal("Speechmatics", SpeechmaticsSpeechDescriptors.Stt.Name);
        Assert.NotEmpty(SpeechmaticsSpeechDescriptors.Stt.Validate(Root()));
        Assert.Empty(SpeechmaticsSpeechDescriptors.Stt.Validate(Root(("Voxa:Speechmatics:ApiKey", "k"))));
    }

    [Fact]
    public void BindOptions_defaults_to_english()
    {
        var o = SpeechmaticsSpeechDescriptors.BindOptions(Root(("Voxa:Speechmatics:ApiKey", "k")));
        Assert.Equal("k", o.ApiKey);
        Assert.Equal("en", o.Language);
    }

    [Fact]
    public void CreateProcessor_builds_a_processor()
        => Assert.NotNull(SpeechmaticsSpeechDescriptors.Stt.CreateProcessor(new NoServices(), Root(("Voxa:Speechmatics:ApiKey", "k"))));

    private sealed class NoServices : IServiceProvider { public object? GetService(Type serviceType) => null; }
}
