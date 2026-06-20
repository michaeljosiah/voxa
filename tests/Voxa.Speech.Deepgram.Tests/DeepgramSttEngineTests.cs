using Microsoft.Extensions.Configuration;
using Voxa.Speech;
using Voxa.Speech.Deepgram;

namespace Voxa.Speech.Deepgram.Tests;

/// <summary>
/// The Deepgram engine's WebSocket plumbing comes from the shared <see cref="WebSocketSttEngine"/> base; the
/// Deepgram-specific seam is the message parser, so that's what's pinned here (plus the descriptor binding).
/// </summary>
public class DeepgramSttEngineTests
{
    private static IConfigurationSection Root(params (string Key, string Value)[] kv)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(kv.ToDictionary(p => p.Key, p => (string?)p.Value))
            .Build()
            .GetSection("Voxa");

    [Fact]
    public void Parse_interim_yields_a_non_final_fragment()
    {
        var f = Assert.Single(DeepgramSttEngine.Parse(
            """{"type":"Results","is_final":false,"channel":{"alternatives":[{"transcript":"hello wor"}]}}""").ToList());
        Assert.Equal("hello wor", f.Text);
        Assert.False(f.IsSegmentFinal);
    }

    [Fact]
    public void Parse_is_final_yields_a_locked_segment()
    {
        var f = Assert.Single(DeepgramSttEngine.Parse(
            """{"type":"Results","is_final":true,"speech_final":true,"channel":{"alternatives":[{"transcript":"hello world"}]}}""").ToList());
        Assert.Equal("hello world", f.Text);
        Assert.True(f.IsSegmentFinal);
    }

    [Theory]
    [InlineData("""{"type":"Results","is_final":true,"channel":{"alternatives":[{"transcript":""}]}}""")] // empty
    [InlineData("""{"type":"Metadata"}""")]
    [InlineData("""{"type":"UtteranceEnd","last_word_end":1.2}""")]
    [InlineData("""{"type":"Results","channel":{"alternatives":[]}}""")] // no alternatives
    public void Parse_ignores_non_transcript_messages(string json)
        => Assert.Empty(DeepgramSttEngine.Parse(json));

    [Fact]
    public void Descriptor_advertises_deepgram()
    {
        Assert.Equal("Deepgram", DeepgramSpeechDescriptors.Stt.Name);
        Assert.Equal("Deepgram", DeepgramSpeechDescriptors.Stt.ConfigSection);
        Assert.Equal(16000, DeepgramSpeechDescriptors.Stt.PreferredInputSampleRate);
    }

    [Fact]
    public void Validate_requires_an_api_key()
    {
        Assert.NotEmpty(DeepgramSpeechDescriptors.Stt.Validate(Root()));
        Assert.Empty(DeepgramSpeechDescriptors.Stt.Validate(Root(("Voxa:Deepgram:ApiKey", "k"))));
    }

    [Fact]
    public void BindOptions_defaults_to_nova3()
    {
        var o = DeepgramSpeechDescriptors.BindOptions(Root(("Voxa:Deepgram:ApiKey", "k")));
        Assert.Equal("k", o.ApiKey);
        Assert.Equal("nova-3", o.Model);
        Assert.Equal(16000, o.InputSampleRate);
    }

    [Fact]
    public void CreateProcessor_builds_a_processor()
    {
        var sp = new EmptyProvider();
        Assert.NotNull(DeepgramSpeechDescriptors.Stt.CreateProcessor(sp, Root(("Voxa:Deepgram:ApiKey", "k"))));
    }

    private sealed class EmptyProvider : IServiceProvider { public object? GetService(Type serviceType) => null; }
}
