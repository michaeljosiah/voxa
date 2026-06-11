using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Voxa.Speech.Piper;

/// <summary>
/// Config-driven descriptor for local piper TTS: <c>"Voxa:Tts": "Piper"</c>. Validation is
/// keyless by design and never downloads (<see cref="VoxaModelCache.IsCached"/> probes only).
/// The announced output rate is resolved from the voice <em>name</em>'s quality suffix at
/// composition time — no model load — per the VDX-001 effective-rate rule; the engine re-verifies
/// against the voice's own config at startup.
/// </summary>
public static class PiperDescriptors
{
    public const string ConfigSectionName = "Piper";

    public static VoxaTtsDescriptor Tts { get; } = new(
        Name: "Piper",
        ConfigSection: ConfigSectionName,
        OutputSampleRate: 22050,
        Validate: Validate,
        CreateProcessor: (sp, root) =>
        {
            var options = PiperOptions.FromConfiguration(root);
            return new TextToSpeechProcessor(
                new PiperTtsEngine(options, CacheFor(sp, root), LoggerFor<PiperTtsEngine>(sp)),
                outputSampleRate: PiperTtsEngine.ExpectedRate(options),
                logger: LoggerFor<TextToSpeechProcessor>(sp));
        })
    {
        ResolveOutputSampleRate = root =>
            PiperTtsEngine.ExpectedRate(PiperOptions.FromConfiguration(root)),

        // Startup warm-up: resolve the executable + voice (downloads on cold cache) and verify
        // the voice's rate against the announced one. The piper process itself spawns lazily on
        // the first synthesis (~0.3 s) — resolution is the expensive part.
        WarmUpAsync = async (sp, root, ct) =>
        {
            await using var engine = new PiperTtsEngine(
                PiperOptions.FromConfiguration(root), CacheFor(sp, root), LoggerFor<PiperTtsEngine>(sp));
            await engine.StartAsync(ct).ConfigureAwait(false);
        },
    };

    private static IReadOnlyList<string> Validate(IConfigurationSection root)
    {
        var errors = new List<string>();
        var options = PiperOptions.FromConfiguration(root);

        if (options.LengthScale is <= 0 or > 4)
            errors.Add($"Voxa:Piper:LengthScale must be in (0, 4]; got {options.LengthScale}.");
        if (options.MaxProcesses < 1)
            errors.Add($"Voxa:Piper:MaxProcesses must be at least 1; got {options.MaxProcesses}.");

        if (!string.IsNullOrEmpty(options.ExecutablePath) && !File.Exists(options.ExecutablePath))
            errors.Add($"Voxa:Piper:ExecutablePath is set to '{options.ExecutablePath}' but no file exists there.");

        if (!string.IsNullOrEmpty(options.VoicePath))
        {
            if (!File.Exists(options.VoicePath))
                errors.Add($"Voxa:Piper:VoicePath is set to '{options.VoicePath}' but no file exists there.");
            else if (!File.Exists(options.VoicePath + ".json"))
                errors.Add($"piper requires the voice config next to the model: '{options.VoicePath}.json' was not found.");

            // Fail closed: with a bring-your-own voice the composer cannot infer the rate from a
            // catalog name, and announcing a guessed rate desyncs every client.
            if (options.OutputSampleRate is null)
                errors.Add("Voxa:Piper:OutputSampleRate is required when Voxa:Piper:VoicePath is set, " +
                           "so the session envelope announces the voice's true rate.");
            return errors;
        }

        if (!PiperVoiceCatalog.TryGet(options.Voice, out var voice))
        {
            errors.Add(
                $"Unknown Voxa:Piper:Voice '{options.Voice}'. Known voices: " +
                $"{string.Join(", ", PiperVoiceCatalog.KnownVoices)}. " +
                "Or set Voxa:Piper:VoicePath to a piper voice of your own.");
            return errors;
        }

        if (options.OutputSampleRate is int rate && rate != voice.SampleRate)
        {
            errors.Add(
                $"Voxa:Piper:OutputSampleRate is set to {rate}, but voice '{voice.Name}' synthesizes at " +
                $"{voice.SampleRate} Hz. Remove the override (the rate is inferred from the voice name).");
        }

        var cacheOptions = VoxaModelCacheOptions.FromConfiguration(root);
        if (cacheOptions.Offline)
        {
            var cache = new VoxaModelCache(cacheOptions);
            foreach (var artifact in new[] { voice.Onnx, voice.Json })
            {
                if (!cache.IsCached(artifact))
                {
                    errors.Add(
                        $"Voxa:Models:Offline is true but '{artifact.Id}' is not in the cache. " +
                        $"Expected at: {cache.PathFor(artifact)}. Provision it out-of-band from {artifact.DownloadUrl} " +
                        $"(SHA-256 {artifact.Sha256}).");
                }
            }

            var hasExecutable =
                !string.IsNullOrEmpty(options.ExecutablePath)
                || PiperExecutableCatalog.FindOnPath() is not null
                || (PiperExecutableCatalog.ForCurrentPlatform() is { } exe && cache.IsCached(exe));
            if (!hasExecutable)
            {
                errors.Add(
                    "Voxa:Models:Offline is true but no piper executable is available (not on PATH, not in the " +
                    "cache, Voxa:Piper:ExecutablePath unset). Provision the pinned build for " +
                    $"{PiperExecutableCatalog.CurrentRid()} into the cache or install piper and set the path.");
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
