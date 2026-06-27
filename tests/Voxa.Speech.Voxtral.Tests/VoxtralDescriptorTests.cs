using Microsoft.Extensions.Configuration;

namespace Voxa.Speech.Voxtral.Tests;

/// <summary>Keyless, side-effect-free validation: a hosting mode must be resolvable and the audio knobs sane.</summary>
public class VoxtralDescriptorTests
{
    private static IConfigurationSection Voxa(params (string Key, string Value)[] settings)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build()
            .GetSection("Voxa");

    [Fact]
    public void No_Hosting_Mode_Is_An_Error()
        => Assert.Contains(VoxtralDescriptors.Stt.Validate(Voxa()), e => e.Contains("hosting mode"));

    [Fact]
    public void ServerUrl_Alone_Is_A_Valid_Connect_Only_Config()
        => Assert.Empty(VoxtralDescriptors.Stt.Validate(Voxa(("Voxa:Voxtral:ServerUrl", "ws://127.0.0.1:8000"))));

    [Fact]
    public void Missing_Launch_Executable_Is_An_Error()
        => Assert.Contains(
            VoxtralDescriptors.Stt.Validate(Voxa(("Voxa:Voxtral:ExecutablePath", "/no/such/voxtral-launcher"))),
            e => e.Contains("ExecutablePath"));

    [Fact]
    public void Out_Of_Range_SampleRate_And_Delay_Are_Errors()
    {
        var errors = VoxtralDescriptors.Stt.Validate(Voxa(
            ("Voxa:Voxtral:ServerUrl", "ws://127.0.0.1:8000"),
            ("Voxa:Voxtral:InputSampleRate", "8000"),
            ("Voxa:Voxtral:DelayMs", "5000")));
        Assert.Contains(errors, e => e.Contains("16000"));
        Assert.Contains(errors, e => e.Contains("2400"));
    }

    [Fact]
    public void Descriptor_Identity_Is_Voxtral_At_16k()
    {
        Assert.Equal("Voxtral", VoxtralDescriptors.Stt.Name);
        Assert.Equal(16000, VoxtralDescriptors.Stt.PreferredInputSampleRate);
    }
}
