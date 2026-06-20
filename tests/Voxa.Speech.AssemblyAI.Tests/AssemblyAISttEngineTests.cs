using Microsoft.Extensions.Configuration;
using Voxa.Speech.AssemblyAI;

namespace Voxa.Speech.AssemblyAI.Tests;

/// <summary>
/// WebSocket plumbing is the shared base's; the AssemblyAI-specific seam is the <c>Turn</c> message parser
/// (plus the descriptor binding).
/// </summary>
public class AssemblyAISttEngineTests
{
    private static IConfigurationSection Root(params (string Key, string Value)[] kv)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(kv.ToDictionary(p => p.Key, p => (string?)p.Value))
            .Build()
            .GetSection("Voxa");

    [Fact]
    public void Parse_interim_turn_is_not_final()
    {
        var f = Assert.Single(AssemblyAISttEngine.Parse(
            """{"type":"Turn","transcript":"hello wor","end_of_turn":false}""").ToList());
        Assert.Equal("hello wor", f.Text);
        Assert.False(f.IsSegmentFinal);
    }

    [Fact]
    public void Parse_end_of_turn_is_final()
    {
        var f = Assert.Single(AssemblyAISttEngine.Parse(
            """{"type":"Turn","transcript":"hello world","end_of_turn":true}""").ToList());
        Assert.Equal("hello world", f.Text);
        Assert.True(f.IsSegmentFinal);
    }

    [Theory]
    [InlineData("""{"type":"Turn","transcript":"","end_of_turn":true}""")] // empty
    [InlineData("""{"type":"Begin","id":"x"}""")]
    [InlineData("""{"type":"Termination"}""")]
    public void Parse_ignores_non_transcript_messages(string json)
        => Assert.Empty(AssemblyAISttEngine.Parse(json));

    [Fact]
    public void Descriptor_advertises_assemblyai()
    {
        Assert.Equal("AssemblyAI", AssemblyAISpeechDescriptors.Stt.Name);
        Assert.Equal("AssemblyAI", AssemblyAISpeechDescriptors.Stt.ConfigSection);
    }

    [Fact]
    public void Validate_requires_an_api_key()
    {
        Assert.NotEmpty(AssemblyAISpeechDescriptors.Stt.Validate(Root()));
        Assert.Empty(AssemblyAISpeechDescriptors.Stt.Validate(Root(("Voxa:AssemblyAI:ApiKey", "k"))));
    }

    [Fact]
    public void BindOptions_defaults()
    {
        var o = AssemblyAISpeechDescriptors.BindOptions(Root(("Voxa:AssemblyAI:ApiKey", "k")));
        Assert.Equal("k", o.ApiKey);
        Assert.True(o.FormatTurns);
        Assert.Equal(16000, o.InputSampleRate);
    }

    [Fact]
    public void CreateProcessor_builds_a_processor()
        => Assert.NotNull(AssemblyAISpeechDescriptors.Stt.CreateProcessor(new NoServices(), Root(("Voxa:AssemblyAI:ApiKey", "k"))));

    private sealed class NoServices : IServiceProvider { public object? GetService(Type serviceType) => null; }
}
