using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Voxa.AspNetCore;
using Voxa.Speech;
using Voxa.Studio.Audio;

namespace Voxa.Studio.Services;

/// <summary>
/// Studio's composition root (VST-001 WS1). Builds the same container a Voxa server gets from
/// <c>AddVoxa(configuration)</c> — registry, composer, validator, diagnostics hub — plus the
/// Studio-only pieces (audio device, model cache handle). Deliberately NOT a Generic Host:
/// hosted services like the startup guard's eager warm-up must not run here, because Studio
/// never touches the network before the user acts (WS1-A2).
///
/// <para>
/// <see cref="Reconfigure"/> lets the Config view apply a draft (provider swap, agent/LLM
/// credentials) to the LIVE app: the base configuration (appsettings + environment) is
/// re-layered with the user's overrides and the container rebuilds, so the next Talk session
/// composes with the new settings — exactly what a server restart would do, without the restart.
/// </para>
/// </summary>
public sealed class StudioServices : IAsyncDisposable
{
    private readonly IConfiguration _baseConfiguration;
    private IReadOnlyDictionary<string, string?> _overrides = new Dictionary<string, string?>();

    public IConfiguration Configuration { get; private set; }
    public ServiceProvider Provider { get; private set; }
    public IStudioAudioDevice AudioDevice { get; }
    public VoxaModelCache ModelCache { get; private set; }

    public VoxaProviderRegistry Registry => Provider.GetRequiredService<VoxaProviderRegistry>();

    /// <summary>Raised after <see cref="Reconfigure"/> swaps the container — views refresh from it.</summary>
    public event Action? Reconfigured;

    public StudioServices(IConfiguration? configuration = null, IStudioAudioDevice? audioDevice = null)
    {
        _baseConfiguration = configuration ?? new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddEnvironmentVariables()
            .Build();

        AudioDevice = audioDevice ?? StudioAudioDevice.CreatePlatformDefault();

        (Configuration, Provider, ModelCache) = Build(_baseConfiguration, _overrides);
    }

    /// <summary>
    /// Re-layer the base configuration with <paramref name="overrides"/> and rebuild the
    /// container. Caller must ensure no Talk session is live (its scope belongs to the old
    /// provider). Overrides REPLACE the previous override set — applying twice doesn't stack.
    /// </summary>
    public void Reconfigure(IReadOnlyDictionary<string, string?> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);

        var old = Provider;
        _overrides = new Dictionary<string, string?>(overrides);
        (Configuration, Provider, ModelCache) = Build(_baseConfiguration, _overrides);
        old.Dispose();

        Reconfigured?.Invoke();
    }

    private static (IConfiguration, ServiceProvider, VoxaModelCache) Build(
        IConfiguration baseConfiguration, IReadOnlyDictionary<string, string?> overrides)
    {
        var configuration = new ConfigurationBuilder()
            .AddConfiguration(baseConfiguration)
            .AddInMemoryCollection(overrides.Where(p => !string.IsNullOrEmpty(p.Value)))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddDebug().SetMinimumLevel(LogLevel.Information));
        // A WebApplicationBuilder registers IConfiguration implicitly; a bare ServiceCollection
        // does not — and the meta-package's DefaultAgentFactory resolves it.
        services.AddSingleton<IConfiguration>(configuration);
        services.AddVoxa(configuration); // the meta-package overload: every built-in provider
        var provider = services.BuildServiceProvider();

        // One cache handle for the Models view — the same options the engine descriptors
        // resolve, so Studio manages exactly the directory the pipeline reads.
        var cache = new VoxaModelCache(
            VoxaModelCacheOptions.FromConfiguration(configuration.GetSection("Voxa")));

        return (configuration, provider, cache);
    }

    public TalkSession CreateTalkSession() => TalkSession.Create(Provider, AudioDevice);

    public async ValueTask DisposeAsync()
    {
        await AudioDevice.DisposeAsync().ConfigureAwait(false);
        await Provider.DisposeAsync().ConfigureAwait(false);
    }
}
