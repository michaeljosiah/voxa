using Microsoft.Extensions.Configuration;
using Voxa.Audio.Diarization.Onnx;
using Voxa.Speech;
using Voxa.Speech.Kokoro;
using Voxa.Speech.Piper;
using Voxa.Speech.WhisperCpp;

namespace Voxa.Studio.Services;

/// <summary>
/// Maps the active <c>Voxa:*</c> configuration to the catalog artifacts it needs, so the Talk
/// view can prefetch with visible progress before the first session, and the Models view can
/// offer "download everything" for air-gap provisioning. Explicit-path overrides (ModelPath /
/// VoicePath / ExecutablePath) bypass the cache by design and contribute nothing here; unknown
/// (non-catalog) names are skipped — the engine's own validation reports those.
/// </summary>
public static class ActiveConfigArtifacts
{
    /// <summary>Artifacts the CURRENT config needs and which are not yet cached.</summary>
    public static IReadOnlyList<VoxaModelArtifact> Missing(IConfiguration configuration, VoxaModelCache cache)
        => ForActiveConfig(configuration).Where(a => !cache.IsCached(a)).ToList();

    /// <summary>Every artifact the active Stt/Tts selection resolves through the cache.</summary>
    public static IReadOnlyList<VoxaModelArtifact> ForActiveConfig(IConfiguration configuration)
    {
        var artifacts = new List<VoxaModelArtifact>();
        var voxa = configuration.GetSection("Voxa");

        if (string.Equals(voxa["Stt"], "WhisperCpp", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(voxa["WhisperCpp:ModelPath"])
            && WhisperCppModelCatalog.TryGet(voxa["WhisperCpp:Model"] ?? "tiny.en", out var ggml))
        {
            artifacts.Add(ggml);
        }

        var tts = voxa["Tts"];
        if (string.Equals(tts, "Piper", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(voxa["Piper:VoicePath"])
                && PiperVoiceCatalog.TryGet(voxa["Piper:Voice"] ?? "en_US-amy-low", out var voice))
            {
                artifacts.Add(voice.Onnx);
                artifacts.Add(voice.Json);
            }
            if (string.IsNullOrEmpty(voxa["Piper:ExecutablePath"])
                && PiperExecutableCatalog.ForCurrentPlatform() is { } piperExe)
            {
                artifacts.Add(piperExe);
            }
        }
        else if (string.Equals(tts, "Kokoro", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(voxa["Kokoro:ModelPath"])
                && KokoroCatalog.TryGetModel(voxa["Kokoro:Precision"] ?? "int8", out var model))
            {
                artifacts.Add(model);
            }
            if (KokoroCatalog.TryGetVoice(voxa["Kokoro:Voice"] ?? "af_heart", out var style))
            {
                artifacts.Add(style);
            }
            if (string.IsNullOrEmpty(voxa["Kokoro:EspeakPath"])
                && KokoroCatalog.FindEspeakOnPath() is null
                && KokoroCatalog.EspeakForCurrentPlatform() is { } espeak)
            {
                artifacts.Add(espeak);
            }
        }

        return artifacts;
    }

    /// <summary>
    /// The full pinned catalog union for THIS machine (current RID only) — the Models view's
    /// "prefetch all" set for air-gapped provisioning.
    /// </summary>
    public static IReadOnlyList<VoxaModelArtifact> FullCatalog()
    {
        var artifacts = new List<VoxaModelArtifact>();

        foreach (var model in WhisperCppModelCatalog.KnownModels)
            if (WhisperCppModelCatalog.TryGet(model, out var a)) artifacts.Add(a);

        foreach (var name in PiperVoiceCatalog.KnownVoices)
            if (PiperVoiceCatalog.TryGet(name, out var v)) { artifacts.Add(v.Onnx); artifacts.Add(v.Json); }
        if (PiperExecutableCatalog.ForCurrentPlatform() is { } piperExe) artifacts.Add(piperExe);

        foreach (var precision in KokoroCatalog.KnownPrecisions)
            if (KokoroCatalog.TryGetModel(precision, out var m)) artifacts.Add(m);
        foreach (var voice in KokoroCatalog.KnownVoices)
            if (KokoroCatalog.TryGetVoice(voice, out var s)) artifacts.Add(s);
        if (KokoroCatalog.EspeakForCurrentPlatform() is { } espeak) artifacts.Add(espeak);

        // Speaker diarization (VLS-005 WS2): the pinned pyannote segmentation model, so it can be
        // prefetched / verified / purged from the Models page like any other artifact.
        artifacts.Add(PyannoteSegmentationCatalog.Model);

        return artifacts;
    }
}
