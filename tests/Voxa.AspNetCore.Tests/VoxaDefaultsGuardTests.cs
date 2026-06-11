using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Voxa.Speech;

namespace Voxa.AspNetCore.Tests;

/// <summary>
/// Regression tests for the VoxaDefaultsGuard agent probe. The guard must not treat the mere
/// presence of an IVoiceAgentFactory as a usable agent — the Voxa meta-package always registers
/// one, so presence alone would defer provider/credential failures to the first WebSocket request.
/// </summary>
public class VoxaDefaultsGuardTests
{
    private sealed class RejectingFactory : IVoiceAgentFactory
    {
        public AIAgent Create(HttpContext context, VoxaAgentOptions options)
            => throw new NotSupportedException();

        public IReadOnlyList<string> Validate(VoxaAgentOptions options)
            => ["factory says: not usable with these options"];
    }

    /// <summary>Relies on the default interface implementation of Validate (no errors).</summary>
    private sealed class NonValidatingFactory : IVoiceAgentFactory
    {
        public AIAgent Create(HttpContext context, VoxaAgentOptions options)
            => throw new NotSupportedException();
    }

    private sealed class FakeChatClient : Microsoft.Extensions.AI.IChatClient
    {
        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static VoxaDefaultsGuard Guard(
        VoxaOptions options,
        Action<IServiceCollection>? configureServices = null)
    {
        var services = new ServiceCollection();
        configureServices?.Invoke(services);
        return new VoxaDefaultsGuard(
            Options.Create(options),
            new VoxaProviderRegistry(),
            services.BuildServiceProvider());
    }

    private static VoxaOptions OptionsWithProviders() => new() { Stt = "Fake", Tts = "Fake" };

    [Fact]
    public async Task Armed_Guard_Fails_When_Factory_Validate_Reports_Errors()
    {
        var guard = Guard(OptionsWithProviders(),
            s => s.AddSingleton<IVoiceAgentFactory, RejectingFactory>());
        guard.Arm();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => guard.StartAsync(CancellationToken.None));
        Assert.Contains("factory says", ex.Message);
    }

    [Fact]
    public async Task Armed_Guard_Passes_When_Factory_Cannot_Validate_Ahead_Of_Time()
    {
        var guard = Guard(OptionsWithProviders(),
            s => s.AddSingleton<IVoiceAgentFactory, NonValidatingFactory>());
        guard.Arm();

        await guard.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Armed_Guard_Skips_Factory_Validation_When_Agent_Is_In_Di()
    {
        var guard = Guard(OptionsWithProviders(), s =>
        {
            s.AddSingleton<Microsoft.Extensions.AI.IChatClient>(new FakeChatClient());
            s.AddSingleton<IVoiceAgentFactory, RejectingFactory>();
        });
        guard.Arm();

        await guard.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Armed_Guard_Fails_When_No_Agent_Source_Exists()
    {
        var guard = Guard(OptionsWithProviders());
        guard.Arm();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => guard.StartAsync(CancellationToken.None));
        Assert.Contains("needs an agent", ex.Message);
    }

    [Fact]
    public async Task Unarmed_Guard_Never_Validates()
    {
        var guard = Guard(new VoxaOptions(),
            s => s.AddSingleton<IVoiceAgentFactory, RejectingFactory>());

        await guard.StartAsync(CancellationToken.None);
    }

    // ── Meta-package end-to-end: the exact reported scenario ───────────────

    private static (VoxaDefaultsGuard Guard, ServiceProvider Sp) MetaPackageGuard(
        params (string Key, string? Value)[] config)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config.ToDictionary(p => p.Key, p => p.Value))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration);
        services.AddVoxa(configuration); // meta-package 2-arg: registers the default agent factory

        var sp = services.BuildServiceProvider();
        var guard = sp.GetRequiredService<VoxaDefaultsGuard>();
        guard.Arm();
        return (guard, sp);
    }

    [Fact]
    public async Task MetaPackage_Guard_Fails_At_Startup_When_Agent_ApiKey_Is_Missing()
    {
        var (guard, sp) = MetaPackageGuard(
            ("Voxa:Stt", "Azure"),
            ("Voxa:Tts", "Azure"),
            ("Voxa:AzureSpeech:SubscriptionKey", "key"),
            ("Voxa:AzureSpeech:Region", "westeurope"),
            ("Voxa:Agent:Provider", "OpenAI"));
        using var _ = sp;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => guard.StartAsync(CancellationToken.None));
        Assert.Contains("Voxa:OpenAI:ApiKey", ex.Message);
    }

    [Fact]
    public async Task MetaPackage_Guard_Fails_At_Startup_For_Unsupported_Agent_Provider()
    {
        var (guard, sp) = MetaPackageGuard(
            ("Voxa:Stt", "Azure"),
            ("Voxa:Tts", "Azure"),
            ("Voxa:AzureSpeech:SubscriptionKey", "key"),
            ("Voxa:AzureSpeech:Region", "westeurope"),
            ("Voxa:Agent:Provider", "Anthropic"),
            ("Voxa:Agent:ApiKey", "some-key"));
        using var _ = sp;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => guard.StartAsync(CancellationToken.None));
        Assert.Contains("Anthropic", ex.Message);
        Assert.Contains("OpenAI", ex.Message);
    }

    [Fact]
    public async Task MetaPackage_Guard_Passes_When_Agent_Is_Fully_Configured()
    {
        var (guard, sp) = MetaPackageGuard(
            ("Voxa:Stt", "OpenAI"),
            ("Voxa:Tts", "OpenAI"),
            ("Voxa:OpenAI:ApiKey", "sk-test"),
            ("Voxa:Agent:Provider", "OpenAI"));
        using var _ = sp;

        await guard.StartAsync(CancellationToken.None);
    }
}
