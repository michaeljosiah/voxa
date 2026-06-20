using Microsoft.Extensions.Configuration;
using Voxa.Speech.Groq;

namespace Voxa.Speech.Groq.Tests;

/// <summary>
/// The Groq descriptor reuses the OpenAI Whisper engine, so the only Groq-specific behaviour to pin is
/// the config binding: the Groq base URL + Whisper-turbo model defaults, override handling, and that the
/// descriptor validates/builds. (Transcription itself is covered by the OpenAI engine's own tests.)
/// </summary>
public class GroqSpeechDescriptorsTests
{
    private sealed class NoServices : IServiceProvider { public object? GetService(Type serviceType) => null; }

    private static IConfigurationSection Root(params (string Key, string Value)[] kv)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(kv.ToDictionary(p => p.Key, p => (string?)p.Value))
            .Build()
            .GetSection("Voxa");

    [Fact]
    public void Descriptor_advertises_groq()
    {
        Assert.Equal("Groq", GroqSpeechDescriptors.Stt.Name);
        Assert.Equal("Groq", GroqSpeechDescriptors.Stt.ConfigSection);
        Assert.Equal(16000, GroqSpeechDescriptors.Stt.PreferredInputSampleRate);
    }

    [Fact]
    public void Validate_requires_an_api_key()
    {
        Assert.NotEmpty(GroqSpeechDescriptors.Stt.Validate(Root()));
        Assert.Empty(GroqSpeechDescriptors.Stt.Validate(Root(("Voxa:Groq:ApiKey", "gsk_x"))));
    }

    [Fact]
    public void BindOptions_targets_groq_with_the_turbo_model_by_default()
    {
        var o = GroqSpeechDescriptors.BindOptions(Root(("Voxa:Groq:ApiKey", "gsk_x")));
        Assert.Equal("gsk_x", o.ApiKey);
        Assert.Equal("https://api.groq.com/openai/v1", o.ApiBaseUrl);
        Assert.Equal("whisper-large-v3-turbo", o.SttModel);
    }

    [Fact]
    public void BindOptions_honors_model_and_base_url_overrides()
    {
        var o = GroqSpeechDescriptors.BindOptions(Root(
            ("Voxa:Groq:ApiKey", "gsk_x"),
            ("Voxa:Groq:SttModel", "whisper-large-v3"),
            ("Voxa:Groq:ApiBaseUrl", "https://proxy.internal/v1"),
            ("Voxa:Groq:SttLanguage", "en")));
        Assert.Equal("whisper-large-v3", o.SttModel);
        Assert.Equal("https://proxy.internal/v1", o.ApiBaseUrl);
        Assert.Equal("en", o.SttLanguage);
    }

    [Fact]
    public void CreateProcessor_builds_a_processor()
        => Assert.NotNull(GroqSpeechDescriptors.Stt.CreateProcessor(new NoServices(), Root(("Voxa:Groq:ApiKey", "gsk_x"))));
}
