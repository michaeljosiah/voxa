using Microsoft.Extensions.Configuration;
using Voxa.Speech.Aws;

namespace Voxa.Speech.Aws.Tests;

/// <summary>The streaming engine needs live AWS credentials to exercise; the unit-testable seam is the descriptor.</summary>
public class AwsSpeechDescriptorsTests
{
    private static IConfigurationSection Root(params (string Key, string Value)[] kv)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(kv.ToDictionary(p => p.Key, p => (string?)p.Value))
            .Build()
            .GetSection("Voxa");

    [Fact]
    public void Descriptor_advertises_aws()
    {
        Assert.Equal("Aws", AwsSpeechDescriptors.Stt.Name);
        Assert.Equal("Aws", AwsSpeechDescriptors.Stt.ConfigSection);
        Assert.Equal(16000, AwsSpeechDescriptors.Stt.PreferredInputSampleRate);
    }

    [Fact]
    public void Validate_allows_no_keys_default_chain_and_full_pair_but_rejects_half()
    {
        Assert.Empty(AwsSpeechDescriptors.Stt.Validate(Root()));                                    // default chain
        Assert.Empty(AwsSpeechDescriptors.Stt.Validate(Root(("Voxa:Aws:AccessKeyId", "a"), ("Voxa:Aws:SecretAccessKey", "s"))));
        Assert.NotEmpty(AwsSpeechDescriptors.Stt.Validate(Root(("Voxa:Aws:AccessKeyId", "a"))));    // half a pair
    }

    [Fact]
    public void BindOptions_defaults()
    {
        var o = AwsSpeechDescriptors.BindOptions(Root());
        Assert.Equal("us-east-1", o.Region);
        Assert.Equal("en-US", o.Language);
        Assert.Equal(16000, o.InputSampleRate);
        Assert.Null(o.AccessKeyId);
    }

    [Fact]
    public void BindOptions_honors_overrides()
    {
        var o = AwsSpeechDescriptors.BindOptions(Root(
            ("Voxa:Aws:Region", "eu-west-1"),
            ("Voxa:Aws:Language", "es-ES")));
        Assert.Equal("eu-west-1", o.Region);
        Assert.Equal("es-ES", o.Language);
    }
}
