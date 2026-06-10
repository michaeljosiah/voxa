using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Voxa.Speech;

namespace Voxa.AspNetCore.Tests;

/// <summary>
/// Defensive-behavior tests for the VDX-001 configuration surface: AddVoxa idempotency,
/// validator provider/VAD checks, and tuning-profile resolution edge cases.
/// </summary>
public class VoxaConfigurationTests
{
    private static IConfiguration Config(params (string Key, string Value)[] pairs)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => (string?)p.Value))
            .Build();

    private static VoxaSttDescriptor Stt(string name) => new(
        Name: name, ConfigSection: name, PreferredInputSampleRate: 16000,
        Validate: _ => [],
        CreateProcessor: (_, _) => throw new NotSupportedException("test descriptor"));

    private static VoxaTtsDescriptor Tts(string name) => new(
        Name: name, ConfigSection: name, OutputSampleRate: 24000,
        Validate: _ => [],
        CreateProcessor: (_, _) => throw new NotSupportedException("test descriptor"));

    private static VoxaVadDescriptor Vad(string name) => new(
        Name: name,
        CreateProcessor: (_, _) => throw new NotSupportedException("test descriptor"));

    // ── AddVoxa idempotency ─────────────────────────────────────────────────

    [Fact]
    public void AddVoxa_Called_Twice_Merges_Descriptors_Into_One_Registry()
    {
        var services = new ServiceCollection();
        var config = Config();

        services.AddVoxa(config, v => v.AddProvider(Stt("First")));
        services.AddVoxa(config, v => v.AddProvider(Tts("Second")));

        using var sp = services.BuildServiceProvider();
        var registry = sp.GetRequiredService<VoxaProviderRegistry>();

        Assert.Contains("First", registry.SttNames, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Second", registry.TtsNames, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddVoxa_Called_Twice_Registers_Guard_Hosted_Service_Once()
    {
        var services = new ServiceCollection();
        var config = Config();

        services.AddVoxa(config, _ => { });
        services.AddVoxa(config, _ => { });

        var hostedServices = services.Count(d => d.ServiceType == typeof(IHostedService));
        Assert.Equal(1, hostedServices);
    }

    // ── Validator: VAD engine ───────────────────────────────────────────────

    private static VoxaOptionsValidator Validator(VoxaProviderRegistry registry, IConfiguration? config = null)
        => new(registry, config ?? Config());

    [Theory]
    [InlineData("Silero")]
    [InlineData("silero")]
    [InlineData("SILENCEGATE")]
    [InlineData("none")]
    public void Validator_Accepts_BuiltIn_Vad_Engines_Case_Insensitively(string engine)
    {
        var result = Validator(new VoxaProviderRegistry()).Validate(null, new VoxaOptions
        {
            Vad = new VoxaVadOptions { Engine = engine },
        });

        Assert.True(result.Succeeded, result.FailureMessage);
    }

    [Fact]
    public void Validator_Accepts_Custom_Vad_Engine_When_Registered()
    {
        var registry = new VoxaProviderRegistry();
        registry.Add(Vad("MyCustomVad"));

        var result = Validator(registry).Validate(null, new VoxaOptions
        {
            Vad = new VoxaVadOptions { Engine = "mycustomvad" },
        });

        Assert.True(result.Succeeded, result.FailureMessage);
    }

    [Fact]
    public void Validator_Rejects_Unknown_Vad_Engine_With_Valid_Values_Listed()
    {
        var result = Validator(new VoxaProviderRegistry()).Validate(null, new VoxaOptions
        {
            Vad = new VoxaVadOptions { Engine = "Bogus" },
        });

        Assert.True(result.Failed);
        Assert.Contains("Bogus", result.FailureMessage);
        Assert.Contains("SilenceGate", result.FailureMessage);
    }

    [Fact]
    public void Validator_Rejects_Unknown_Provider_Name()
    {
        var registry = new VoxaProviderRegistry();
        registry.Add(Stt("OpenAI"));

        var result = Validator(registry).Validate(null, new VoxaOptions { Stt = "Tipo" });

        Assert.True(result.Failed);
        Assert.Contains("Tipo", result.FailureMessage);
    }

    [Fact]
    public void Validator_Rejects_Unknown_Profile()
    {
        var result = Validator(new VoxaProviderRegistry()).Validate(null, new VoxaOptions
        {
            Profile = "Turbo",
        });

        Assert.True(result.Failed);
        Assert.Contains("Turbo", result.FailureMessage);
        Assert.Contains("LowLatency", result.FailureMessage);
    }

    // ── Validation wiring end-to-end ────────────────────────────────────────

    [Fact]
    public void AddVoxa_Options_Validation_Fails_For_Unregistered_Stt()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVoxa(Config(("Voxa:Stt", "Nope")), _ => { });

        using var sp = services.BuildServiceProvider();
        var ex = Assert.Throws<OptionsValidationException>(
            () => sp.GetRequiredService<IOptions<VoxaOptions>>().Value);
        Assert.Contains("Nope", ex.Message);
    }
}
