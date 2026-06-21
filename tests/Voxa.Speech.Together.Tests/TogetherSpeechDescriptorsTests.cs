using Microsoft.Extensions.Configuration;
using Voxa.Speech.Together;

namespace Voxa.Speech.Together.Tests;

/// <summary>
/// The Together descriptor reuses the OpenAI Whisper engine, so the Together-specific behaviour to pin is
/// the config binding: the Together base URL + Whisper model defaults, override handling, and that the
/// descriptor validates/builds.
/// </summary>
public class TogetherSpeechDescriptorsTests
{
    private sealed class NoServices : IServiceProvider { public object? GetService(Type serviceType) => null; }

    private static IConfigurationSection Root(params (string Key, string Value)[] kv)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(kv.ToDictionary(p => p.Key, p => (string?)p.Value))
            .Build()
            .GetSection("Voxa");

    [Fact]
    public void Descriptor_advertises_together()
    {
        Assert.Equal("Together", TogetherSpeechDescriptors.Stt.Name);
        Assert.Equal("Together", TogetherSpeechDescriptors.Stt.ConfigSection);
        Assert.Equal(16000, TogetherSpeechDescriptors.Stt.PreferredInputSampleRate);
    }

    [Fact]
    public void Validate_requires_an_api_key()
    {
        Assert.NotEmpty(TogetherSpeechDescriptors.Stt.Validate(Root()));
        Assert.Empty(TogetherSpeechDescriptors.Stt.Validate(Root(("Voxa:Together:ApiKey", "x"))));
    }

    [Fact]
    public void BindOptions_targets_together_with_whisper_v3_by_default()
    {
        var o = TogetherSpeechDescriptors.BindOptions(Root(("Voxa:Together:ApiKey", "x")));
        Assert.Equal("x", o.ApiKey);
        Assert.Equal("https://api.together.xyz/v1", o.ApiBaseUrl);
        Assert.Equal("openai/whisper-large-v3", o.SttModel);
    }

    [Fact]
    public void BindOptions_honors_overrides()
    {
        var o = TogetherSpeechDescriptors.BindOptions(Root(
            ("Voxa:Together:ApiKey", "x"),
            ("Voxa:Together:ApiBaseUrl", "https://proxy.internal/v1")));
        Assert.Equal("https://proxy.internal/v1", o.ApiBaseUrl);
    }

    [Fact]
    public void CreateProcessor_builds_a_processor()
        => Assert.NotNull(TogetherSpeechDescriptors.Stt.CreateProcessor(new NoServices(), Root(("Voxa:Together:ApiKey", "x"))));
}
