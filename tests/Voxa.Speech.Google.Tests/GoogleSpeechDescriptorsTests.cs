using Microsoft.Extensions.Configuration;
using Voxa.Speech.Google;

namespace Voxa.Speech.Google.Tests;

/// <summary>The gRPC engine needs live credentials to exercise; the unit-testable seam is the descriptor binding.</summary>
public class GoogleSpeechDescriptorsTests
{
    private static IConfigurationSection Root(params (string Key, string Value)[] kv)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(kv.ToDictionary(p => p.Key, p => (string?)p.Value))
            .Build()
            .GetSection("Voxa");

    [Fact]
    public void Descriptor_advertises_google()
    {
        Assert.Equal("Google", GoogleSpeechDescriptors.Stt.Name);
        Assert.Equal("Google", GoogleSpeechDescriptors.Stt.ConfigSection);
        Assert.Equal(16000, GoogleSpeechDescriptors.Stt.PreferredInputSampleRate);
    }

    [Fact]
    public void Validate_requires_a_project_id()
    {
        Assert.NotEmpty(GoogleSpeechDescriptors.Stt.Validate(Root()));
        Assert.Empty(GoogleSpeechDescriptors.Stt.Validate(Root(("Voxa:Google:ProjectId", "p"))));
    }

    [Fact]
    public void BindOptions_defaults()
    {
        var o = GoogleSpeechDescriptors.BindOptions(Root(("Voxa:Google:ProjectId", "p")));
        Assert.Equal("p", o.ProjectId);
        Assert.Equal("global", o.Location);
        Assert.Equal("en-US", o.Language);
        Assert.Equal("long", o.Model);
        Assert.Equal("_", o.Recognizer);
    }

    [Fact]
    public void BindOptions_honors_overrides()
    {
        var o = GoogleSpeechDescriptors.BindOptions(Root(
            ("Voxa:Google:ProjectId", "p"),
            ("Voxa:Google:Location", "us-central1"),
            ("Voxa:Google:Language", "fr-FR"),
            ("Voxa:Google:Model", "telephony")));
        Assert.Equal("us-central1", o.Location);
        Assert.Equal("fr-FR", o.Language);
        Assert.Equal("telephony", o.Model);
    }
}
