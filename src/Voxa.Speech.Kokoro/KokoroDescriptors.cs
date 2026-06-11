using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Voxa.Speech.Kokoro;

/// <summary>
/// Config-driven descriptor for local Kokoro TTS: <c>"Voxa:Tts": "Kokoro"</c>. Validation is
/// keyless by design and never downloads. The output rate is a model constant (24 000 Hz — which
/// equals the cloud-TTS default, so swapping ElevenLabs→Kokoro leaves the session envelope
/// unchanged); overrides are rejected at startup, the same rule as Whisper's 16 kHz input.
/// </summary>
public static class KokoroDescriptors
{
    public const string ConfigSectionName = "Kokoro";

    public static VoxaTtsDescriptor Tts { get; } = new(
        Name: "Kokoro",
        ConfigSection: ConfigSectionName,
        OutputSampleRate: KokoroCatalog.OutputSampleRate,
        Validate: Validate,
        CreateProcessor: (sp, root) => new TextToSpeechProcessor(
            new KokoroTtsEngine(
                KokoroOptions.FromConfiguration(root),
                CacheFor(sp, root),
                LoggerFor<KokoroTtsEngine>(sp)),
            outputSampleRate: KokoroCatalog.OutputSampleRate,
            logger: LoggerFor<TextToSpeechProcessor>(sp)))
    {
        // No name inference needed — the rate is a model constant. The hook still pins it so a
        // config override can never desync the envelope (Validate rejects overrides anyway).
        ResolveOutputSampleRate = _ => KokoroCatalog.OutputSampleRate,

        // Startup warm-up: resolve model + voice + espeak (downloads on cold cache) and load the
        // shared InferenceSession — weights stay in the static session cache for every connection.
        WarmUpAsync = async (sp, root, ct) =>
        {
            await using var engine = new KokoroTtsEngine(
                KokoroOptions.FromConfiguration(root), CacheFor(sp, root), LoggerFor<KokoroTtsEngine>(sp));
            await engine.StartAsync(ct).ConfigureAwait(false);
        },
    };

    private static IReadOnlyList<string> Validate(IConfigurationSection root)
    {
        var errors = new List<string>();
        var s = root.GetSection(ConfigSectionName);
        var options = KokoroOptions.FromConfiguration(root);

        var rateOverride = s.GetValue<int?>("OutputSampleRate", null);
        if (rateOverride is int rate && rate != KokoroCatalog.OutputSampleRate)
        {
            errors.Add(
                $"Voxa:Kokoro:OutputSampleRate is set to {rate}, but Kokoro-82M only synthesizes at " +
                $"{KokoroCatalog.OutputSampleRate} Hz. Remove the override.");
        }

        if (options.Speed is <= 0 or > 3)
            errors.Add($"Voxa:Kokoro:Speed must be in (0, 3]; got {options.Speed}.");
        if (options.MaxConcurrentSyntheses < 1)
            errors.Add($"Voxa:Kokoro:MaxConcurrentSyntheses must be at least 1; got {options.MaxConcurrentSyntheses}.");

        var modelKnown = true;
        if (!string.IsNullOrEmpty(options.ModelPath))
        {
            if (!File.Exists(options.ModelPath))
                errors.Add($"Voxa:Kokoro:ModelPath is set to '{options.ModelPath}' but no file exists there.");
            modelKnown = false;
        }
        else if (!KokoroCatalog.TryGetModel(options.Precision, out _))
        {
            errors.Add(
                $"Unknown Voxa:Kokoro:Precision '{options.Precision}'. " +
                $"Valid values: {string.Join(", ", KokoroCatalog.KnownPrecisions)}.");
            modelKnown = false;
        }

        var voiceKnown = true;
        if (!string.IsNullOrEmpty(options.VoicePath))
        {
            if (!File.Exists(options.VoicePath))
                errors.Add($"Voxa:Kokoro:VoicePath is set to '{options.VoicePath}' but no file exists there.");
            voiceKnown = false;
        }
        else if (!KokoroCatalog.TryGetVoice(options.Voice, out _))
        {
            errors.Add(
                $"Unknown Voxa:Kokoro:Voice '{options.Voice}'. Known voices: " +
                $"{string.Join(", ", KokoroCatalog.KnownVoices)}. " +
                "Or set Voxa:Kokoro:VoicePath to a style-vector file of your own.");
            voiceKnown = false;
        }

        if (!string.IsNullOrEmpty(options.EspeakPath) && !File.Exists(options.EspeakPath))
            errors.Add($"Voxa:Kokoro:EspeakPath is set to '{options.EspeakPath}' but no file exists there.");

        var cacheOptions = VoxaModelCacheOptions.FromConfiguration(root);
        if (cacheOptions.Offline)
        {
            var cache = new VoxaModelCache(cacheOptions);

            if (modelKnown && KokoroCatalog.TryGetModel(options.Precision, out var model) && !cache.IsCached(model))
            {
                errors.Add(
                    $"Voxa:Models:Offline is true but the Kokoro {options.Precision} model is not in the cache. " +
                    $"Expected at: {cache.PathFor(model)}. Provision it out-of-band from {model.DownloadUrl} " +
                    $"(SHA-256 {model.Sha256}).");
            }
            if (voiceKnown && KokoroCatalog.TryGetVoice(options.Voice, out var voice) && !cache.IsCached(voice))
            {
                errors.Add(
                    $"Voxa:Models:Offline is true but the Kokoro voice '{options.Voice}' is not in the cache. " +
                    $"Expected at: {cache.PathFor(voice)}. Provision it out-of-band from {voice.DownloadUrl} " +
                    $"(SHA-256 {voice.Sha256}).");
            }

            var hasEspeak =
                !string.IsNullOrEmpty(options.EspeakPath)
                || KokoroCatalog.FindEspeakOnPath() is not null
                || (KokoroCatalog.EspeakForCurrentPlatform() is { } espeak && cache.IsCached(espeak));
            if (!hasEspeak)
            {
                errors.Add(
                    "Voxa:Models:Offline is true but no espeak-ng executable is available (not on PATH, not in " +
                    "the cache, Voxa:Kokoro:EspeakPath unset). Provision the pinned piper-phonemize build for " +
                    $"{KokoroCatalog.CurrentRid()} into the cache, or install espeak-ng and set the path.");
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
