using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Voxa.AspNetCore;
using Voxa.Audio.SmartTurn;
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
///
/// <para>
/// Configuration is THREE layers, lowest priority first: base (appsettings + environment) →
/// <b>secrets</b> (the DPAPI-backed credential store, VST-003) → overrides (the Config draft). Secrets
/// are their own layer precisely because <see cref="Reconfigure"/> REPLACES the override set: if stored
/// keys were overrides, the first Config "Apply" would wipe them. <see cref="ApplySecrets"/> swaps the
/// secrets layer after a Settings Save while leaving the overrides intact.
/// </para>
/// </summary>
public sealed class StudioServices : IAsyncDisposable
{
    private readonly IConfiguration _baseConfiguration;
    private IReadOnlyDictionary<string, string?> _secrets = new Dictionary<string, string?>();
    private IReadOnlyDictionary<string, string?> _overrides = new Dictionary<string, string?>();

    public IConfiguration Configuration { get; private set; }
    public ServiceProvider Provider { get; private set; }
    public IStudioAudioDevice AudioDevice { get; }
    public VoxaModelCache ModelCache { get; private set; }

    /// <summary>The credential store behind the secrets layer (DPAPI on Windows, in-memory elsewhere).</summary>
    public ISecretsStore SecretsStore { get; }

    /// <summary>Activation + credentials facade the Settings dialog and the Config filter share.</summary>
    public ProviderSecretsService Secrets { get; }

    /// <summary>Named pipeline profiles + the active one — the app-wide "which pipeline am I running".</summary>
    public PipelineProfileStore Profiles { get; }

    public VoxaProviderRegistry Registry => Provider.GetRequiredService<VoxaProviderRegistry>();

    /// <summary>Raised after the container is swapped (Reconfigure / ApplySecrets) — views refresh from it.</summary>
    public event Action? Reconfigured;

    public StudioServices(
        IConfiguration? configuration = null,
        IStudioAudioDevice? audioDevice = null,
        ISecretsStore? secretsStore = null,
        ProviderActivationStore? activationStore = null,
        PipelineProfileStore? pipelineProfiles = null)
    {
        _baseConfiguration = configuration ?? new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddEnvironmentVariables()
            .Build();

        AudioDevice = audioDevice ?? StudioAudioDevice.CreatePlatformDefault();

        // Credentials store: a DPAPI-encrypted file on Windows, in-memory elsewhere/tests. Disk read
        // only — no network — so it is safe on the startup path (the "no network before the user acts"
        // rule). Tests inject a MemorySecretsStore + temp activation path for isolation.
        SecretsStore = secretsStore ?? CreateDefaultSecretsStore();
        Secrets = new ProviderSecretsService(SecretsStore, activationStore ?? new ProviderActivationStore());
        Profiles = pipelineProfiles ?? new PipelineProfileStore();

        // Fold the stored secrets AND the saved active profile into the FIRST build, so a returning user
        // reopens on the pipeline they left — live from the first session, no post-construction rebuild.
        _secrets = Secrets.BuildConfigPairs();
        if (Profiles.TryGetActive(out var activePairs))
            _overrides = new Dictionary<string, string?>(activePairs);
        (Configuration, Provider, ModelCache) = Build(_baseConfiguration, _secrets, _overrides);
    }

    private static ISecretsStore CreateDefaultSecretsStore()
    {
        if (OperatingSystem.IsWindows())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return new DpapiSecretsStore(Path.Combine(home, "voxa-secrets.dpapi"));
        }
        return new MemorySecretsStore();
    }

    /// <summary>
    /// Re-layer the base configuration with <paramref name="overrides"/> and rebuild the
    /// container, keeping the current secrets layer. Caller must ensure no Talk session is live
    /// (its scope belongs to the old provider). Overrides REPLACE the previous override set —
    /// applying twice doesn't stack.
    /// </summary>
    public void Reconfigure(IReadOnlyDictionary<string, string?> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);

        var old = Provider;
        _overrides = new Dictionary<string, string?>(overrides);
        (Configuration, Provider, ModelCache) = Build(_baseConfiguration, _secrets, _overrides);
        old.Dispose();

        Reconfigured?.Invoke();
    }

    /// <summary>
    /// Make <paramref name="name"/> the active pipeline app-wide: persist the choice and rebuild the
    /// container from its pairs (or back to the base config when null). Like <see cref="Reconfigure"/>,
    /// the caller must ensure no live session is running; fires <see cref="Reconfigured"/> so views refresh.
    /// </summary>
    public void ActivateProfile(string? name)
    {
        Profiles.SetActive(name);
        Reconfigure(name is not null && Profiles.TryGet(name, out var pairs)
            ? pairs
            : new Dictionary<string, string?>());
    }

    /// <summary>
    /// Swap the secrets layer (after a Settings "Save") and rebuild, keeping the current Config
    /// overrides. This is how stored credentials reach the live container without a restart.
    /// </summary>
    public void ApplySecrets(IReadOnlyDictionary<string, string?> secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);

        var old = Provider;
        _secrets = new Dictionary<string, string?>(secrets);
        (Configuration, Provider, ModelCache) = Build(_baseConfiguration, _secrets, _overrides);
        old.Dispose();

        Reconfigured?.Invoke();
    }

    private static (IConfiguration, ServiceProvider, VoxaModelCache) Build(
        IConfiguration baseConfiguration,
        IReadOnlyDictionary<string, string?> secrets,
        IReadOnlyDictionary<string, string?> overrides)
    {
        var configuration = new ConfigurationBuilder()
            .AddConfiguration(baseConfiguration)
            .AddInMemoryCollection(secrets.Where(p => !string.IsNullOrEmpty(p.Value)))
            .AddInMemoryCollection(overrides.Where(p => !string.IsNullOrEmpty(p.Value)))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddDebug().SetMinimumLevel(LogLevel.Information));
        // A WebApplicationBuilder registers IConfiguration implicitly; a bare ServiceCollection
        // does not — and the meta-package's DefaultAgentFactory resolves it.
        services.AddSingleton<IConfiguration>(configuration);
        services.AddVoxa(configuration); // the meta-package overload: every built-in provider
        // Smart turn is opt-in (a call on the turn-taking path), so it is NOT in AddVoxa — register it
        // here so the Config "Smart turn detection" toggle takes effect. No-ops unless Voxa:SmartTurn
        // selects a provider; the composer then auto-wires the registered ISmartTurnClassifier.
        services.AddVoxaSmartTurn(configuration);
        var provider = services.BuildServiceProvider();

        // One cache handle for the Models view — the same options the engine descriptors
        // resolve, so Studio manages exactly the directory the pipeline reads.
        var cache = new VoxaModelCache(
            VoxaModelCacheOptions.FromConfiguration(configuration.GetSection("Voxa")));

        return (configuration, provider, cache);
    }

    public TalkSession CreateTalkSession() => TalkSession.Create(Provider, AudioDevice);

    /// <summary>
    /// Warm the configured STT/TTS model weights into memory (whisper.cpp caches its
    /// <c>WhisperFactory</c> process-wide, so a later Talk session reuses them — the first turn isn't
    /// a cold start). When <paramref name="cachedOnly"/> is true this NO-OPS if any required artifact is
    /// missing, honoring "no network before the user acts" — which makes it safe on the splash/startup
    /// path and after a Config Apply. Best-effort: a genuinely broken model resurfaces with its
    /// remediation message when the pipeline starts, so failures here are swallowed.
    /// </summary>
    public async Task WarmUpAsync(bool cachedOnly, CancellationToken ct = default)
    {
        if (cachedOnly && ActiveConfigArtifacts.Missing(Configuration, ModelCache).Count > 0)
            return; // a download would be needed — defer to the user-triggered Talk Start

        var voxa = Configuration.GetSection("Voxa");
        var registry = Registry;
        using var scope = Provider.CreateScope();
        var sp = scope.ServiceProvider;
        try
        {
            if (voxa["Stt"] is { } stt && registry.TryGetStt(stt, out var sttDesc) && sttDesc.WarmUpAsync is not null)
                await sttDesc.WarmUpAsync(sp, voxa, ct).ConfigureAwait(false);
            if (voxa["Tts"] is { } tts && registry.TryGetTts(tts, out var ttsDesc) && ttsDesc.WarmUpAsync is not null)
                await ttsDesc.WarmUpAsync(sp, voxa, ct).ConfigureAwait(false);
        }
        catch { /* best-effort warm-up */ }
    }

    public async ValueTask DisposeAsync()
    {
        await AudioDevice.DisposeAsync().ConfigureAwait(false);
        await Provider.DisposeAsync().ConfigureAwait(false);
    }
}
