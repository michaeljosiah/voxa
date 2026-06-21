using Microsoft.Extensions.Configuration;
using Voxa.Speech.Gladia;

namespace Voxa.Speech.Gladia.Tests;

/// <summary>WebSocket + session-init plumbing is the shared base's; the Gladia seam is the transcript parser.</summary>
public class GladiaSttEngineTests
{
    private static IConfigurationSection Root(params (string Key, string Value)[] kv)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(kv.ToDictionary(p => p.Key, p => (string?)p.Value))
            .Build()
            .GetSection("Voxa");

    [Fact]
    public void Parse_non_final_transcript_is_interim()
    {
        var f = Assert.Single(GladiaSttEngine.Parse(
            """{"type":"transcript","data":{"is_final":false,"utterance":{"text":"hi the"}}}""").ToList());
        Assert.Equal("hi the", f.Text);
        Assert.False(f.IsSegmentFinal);
    }

    [Fact]
    public void Parse_final_transcript_is_locked()
    {
        var f = Assert.Single(GladiaSttEngine.Parse(
            """{"type":"transcript","data":{"is_final":true,"utterance":{"text":"hi there"}}}""").ToList());
        Assert.Equal("hi there", f.Text);
        Assert.True(f.IsSegmentFinal);
    }

    [Theory]
    [InlineData("""{"type":"transcript","data":{"is_final":true,"utterance":{"text":""}}}""")] // empty
    [InlineData("""{"type":"audio_chunk","data":{"acknowledged":true}}""")]
    [InlineData("""{"type":"transcript"}""")] // no data
    public void Parse_ignores_non_transcript_messages(string json)
        => Assert.Empty(GladiaSttEngine.Parse(json));

    [Fact]
    public void Descriptor_advertises_gladia_and_validates_key()
    {
        Assert.Equal("Gladia", GladiaSpeechDescriptors.Stt.Name);
        Assert.NotEmpty(GladiaSpeechDescriptors.Stt.Validate(Root()));
        Assert.Empty(GladiaSpeechDescriptors.Stt.Validate(Root(("Voxa:Gladia:ApiKey", "k"))));
    }

    [Fact]
    public void BindOptions_defaults()
    {
        var o = GladiaSpeechDescriptors.BindOptions(Root(("Voxa:Gladia:ApiKey", "k")));
        Assert.Equal("k", o.ApiKey);
        Assert.Equal(16000, o.InputSampleRate);
    }

    [Fact]
    public void CreateProcessor_builds_a_processor()
        => Assert.NotNull(GladiaSpeechDescriptors.Stt.CreateProcessor(new NoServices(), Root(("Voxa:Gladia:ApiKey", "k"))));

    private sealed class NoServices : IServiceProvider { public object? GetService(Type serviceType) => null; }
}
