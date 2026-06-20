using Microsoft.Extensions.Configuration;

namespace Voxa.Speech.Kokoro;

/// <summary>
/// Options for <see cref="KokoroTtsEngine"/>, bound from the <c>Voxa:Kokoro</c> section.
/// No credentials — the local tier's whole point.
/// </summary>
public sealed class KokoroOptions
{
    /// <summary>Catalog voice name (see <see cref="KokoroCatalog"/>). Ignored when <see cref="VoicePath"/> is set.</summary>
    public string Voice { get; set; } = "af_heart";

    /// <summary>Model precision: <c>fp32</c>, <c>fp16</c>, or <c>int8</c>. Size/speed trade (§6.WS3.3).</summary>
    public string Precision { get; set; } = "fp16";

    /// <summary>Explicit Kokoro ONNX path — bypasses the catalog and cache.</summary>
    public string? ModelPath { get; set; }

    /// <summary>Explicit voice style-vector path (raw float32, 510×256) — bypasses the catalog.</summary>
    public string? VoicePath { get; set; }

    /// <summary>Explicit espeak-ng executable. Otherwise: <c>PATH</c> probe, then pinned download.</summary>
    public string? EspeakPath { get; set; }

    /// <summary>
    /// espeak-ng voice for phonemization. Default: inferred from the Kokoro voice prefix
    /// (<c>a*</c> → <c>en-us</c>, <c>b*</c> → <c>en-gb</c>).
    /// </summary>
    public string? EspeakVoice { get; set; }

    /// <summary>Kokoro speed: &gt;1 faster speech. Valid range (0, 3].</summary>
    public double Speed { get; set; } = 1.0;

    /// <summary>
    /// ONNX execution target (VLS-006): <c>cpu</c> (default), <c>auto</c>, <c>cuda</c>, <c>directml</c>,
    /// <c>coreml</c>. GPU EPs require the consuming app to bundle the matching
    /// <c>Microsoft.ML.OnnxRuntime.*</c> package (Voxa never bundles GPU natives); an explicit GPU device
    /// whose provider isn't in the loaded runtime fails loud at startup rather than silently running on CPU.
    /// Parsed via <c>OnnxDeviceParser</c> so the spelling matches every other ONNX engine.
    /// </summary>
    public string? Device { get; set; }

    /// <summary>Process-wide cap on parallel ONNX runs so synthesis can't starve the audio pipeline.</summary>
    public int MaxConcurrentSyntheses { get; set; } = 2;

    /// <summary>The espeak-ng voice to phonemize with.</summary>
    public string ResolveEspeakVoice()
        => EspeakVoice
           ?? (Voice.StartsWith("b", StringComparison.OrdinalIgnoreCase) ? "en-gb" : "en-us");

    /// <summary>Bind from the root <c>"Voxa"</c> configuration section.</summary>
    public static KokoroOptions FromConfiguration(IConfigurationSection voxaRoot)
    {
        var s = voxaRoot.GetSection(KokoroDescriptors.ConfigSectionName);
        return new KokoroOptions
        {
            Voice                  = s["Voice"] ?? "af_heart",
            Precision              = s["Precision"] ?? "fp16",
            ModelPath              = s["ModelPath"],
            VoicePath              = s["VoicePath"],
            EspeakPath             = s["EspeakPath"],
            EspeakVoice            = s["EspeakVoice"],
            Speed                  = s.GetValue("Speed", 1.0),
            Device                 = s["Device"],
            MaxConcurrentSyntheses = s.GetValue("MaxConcurrentSyntheses", 2),
        };
    }
}
