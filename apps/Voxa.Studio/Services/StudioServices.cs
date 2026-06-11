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
/// </summary>
public sealed class StudioServices : IAsyncDisposable
{
    public IConfiguration Configuration { get; }
    public ServiceProvider Provider { get; }
    public IStudioAudioDevice AudioDevice { get; }

    public VoxaProviderRegistry Registry => Provider.GetRequiredService<VoxaProviderRegistry>();
    public VoxaModelCache ModelCache { get; }

    public StudioServices(IConfiguration? configuration = null, IStudioAudioDevice? audioDevice = null)
    {
        Configuration = configuration ?? new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddEnvironmentVariables()
            .Build();

        AudioDevice = audioDevice ?? StudioAudioDevice.CreatePlatformDefault();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddDebug().SetMinimumLevel(LogLevel.Information));
        services.AddVoxa(Configuration); // the meta-package overload: every built-in provider
        Provider = services.BuildServiceProvider();

        // One cache handle for the Models view — the same options the engine descriptors
        // resolve, so Studio manages exactly the directory the pipeline reads.
        ModelCache = new VoxaModelCache(
            VoxaModelCacheOptions.FromConfiguration(Configuration.GetSection("Voxa")));
    }

    public TalkSession CreateTalkSession() => TalkSession.Create(Provider, AudioDevice);

    public async ValueTask DisposeAsync()
    {
        await AudioDevice.DisposeAsync().ConfigureAwait(false);
        await Provider.DisposeAsync().ConfigureAwait(false);
    }
}
