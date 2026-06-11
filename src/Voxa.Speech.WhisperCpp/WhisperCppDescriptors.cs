using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Voxa.Speech.WhisperCpp;

/// <summary>
/// Config-driven descriptor for local whisper.cpp STT: <c>"Voxa:Stt": "WhisperCpp"</c>.
/// Validation is keyless by design — it checks the model catalog, explicit paths, and (in offline
/// mode) cache presence. Validation never downloads: <see cref="VoxaModelCache.IsCached"/> only.
/// </summary>
public static class WhisperCppDescriptors
{
    public const string ConfigSectionName = "WhisperCpp";

    public static VoxaSttDescriptor Stt { get; } = new(
        Name: "WhisperCpp",
        ConfigSection: ConfigSectionName,
        PreferredInputSampleRate: WhisperCppSttEngine.RequiredSampleRate,
        Validate: Validate,
        CreateProcessor: (sp, root) => new SpeechToTextProcessor(
            new WhisperCppSttEngine(
                WhisperCppOptions.FromConfiguration(root),
                CacheFor(sp, root),
                LoggerFor<WhisperCppSttEngine>(sp))))
    {
        // Startup warm-up: resolve the model (downloads on cold cache, progress logged) and load
        // the shared WhisperFactory, so the first caller never pays either. The engine's static
        // factory cache keeps the weights after this throwaway engine is disposed.
        WarmUpAsync = async (sp, root, ct) =>
        {
            await using var engine = new WhisperCppSttEngine(
                WhisperCppOptions.FromConfiguration(root), CacheFor(sp, root), LoggerFor<WhisperCppSttEngine>(sp));
            await engine.StartAsync(ct).ConfigureAwait(false);
        },
    };

    private static IReadOnlyList<string> Validate(IConfigurationSection root)
    {
        var errors = new List<string>();
        var s = root.GetSection(ConfigSectionName);
        var options = WhisperCppOptions.FromConfiguration(root);

        // whisper.cpp is 16 kHz-only. Reject overrides at startup, not with garbage transcripts
        // at runtime (the override would also desync the session envelope's announced rate).
        var rateOverride = s.GetValue<int?>("InputSampleRate", null);
        if (rateOverride is int rate && rate != WhisperCppSttEngine.RequiredSampleRate)
        {
            errors.Add(
                $"Voxa:WhisperCpp:InputSampleRate is set to {rate}, but whisper.cpp models only accept " +
                $"{WhisperCppSttEngine.RequiredSampleRate} Hz mono. Remove the override.");
        }

        if (!string.IsNullOrEmpty(options.ModelPath))
        {
            // Explicit path wins and bypasses the catalog — but a wrong explicit path is a config
            // bug, not a download trigger.
            if (!File.Exists(options.ModelPath))
                errors.Add($"Voxa:WhisperCpp:ModelPath is set to '{options.ModelPath}' but no file exists there.");
            return errors;
        }

        if (!WhisperCppModelCatalog.TryGet(options.Model, out var artifact))
        {
            errors.Add(
                $"Unknown Voxa:WhisperCpp:Model '{options.Model}'. Known models: " +
                $"{string.Join(", ", WhisperCppModelCatalog.KnownModels)}. " +
                "Or set Voxa:WhisperCpp:ModelPath to a GGML file of your own.");
            return errors;
        }

        var cacheOptions = VoxaModelCacheOptions.FromConfiguration(root);
        if (cacheOptions.Offline)
        {
            var cache = new VoxaModelCache(cacheOptions);
            if (!cache.IsCached(artifact))
            {
                errors.Add(
                    $"Voxa:Models:Offline is true but the whisper model '{options.Model}' is not in the cache. " +
                    $"Expected at: {cache.PathFor(artifact)}. Provision it out-of-band from {artifact.DownloadUrl} " +
                    $"(SHA-256 {artifact.Sha256}), or set Voxa:Models:Offline to false.");
            }
        }

        return errors;
    }

    internal static VoxaModelCache CacheFor(IServiceProvider sp, IConfigurationSection root)
        => new(
            VoxaModelCacheOptions.FromConfiguration(root),
            (sp.GetService(typeof(IVoxaHttpClientProvider)) as IVoxaHttpClientProvider)?.Resolve(),
            LoggerFor<VoxaModelCache>(sp));

    internal static ILogger? LoggerFor<T>(IServiceProvider sp)
        => (sp.GetService(typeof(ILoggerFactory)) as ILoggerFactory)?.CreateLogger(typeof(T).FullName!);
}
