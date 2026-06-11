using Microsoft.Extensions.Configuration;

namespace Voxa.Speech.Piper;

/// <summary>
/// Options for <see cref="PiperTtsEngine"/>, bound from the <c>Voxa:Piper</c> section.
/// No credentials — the local tier's whole point.
/// </summary>
public sealed class PiperOptions
{
    /// <summary>Catalog voice name (see <see cref="PiperVoiceCatalog"/>). Ignored when <see cref="VoicePath"/> is set.</summary>
    public string Voice { get; set; } = "en_US-lessac-medium";

    /// <summary>
    /// Explicit voice <c>.onnx</c> path (the <c>.onnx.json</c> config is expected alongside it) —
    /// bypasses the catalog and cache. Requires an explicit <see cref="OutputSampleRate"/> so the
    /// session envelope cannot silently desync from an unknown voice's rate.
    /// </summary>
    public string? VoicePath { get; set; }

    /// <summary>Explicit piper executable path. Otherwise: <c>PATH</c> probe, then first-run download.</summary>
    public string? ExecutablePath { get; set; }

    /// <summary>
    /// Override of the announced output rate. Default: inferred from the voice name's quality
    /// suffix (<c>-low</c>/<c>-x_low</c> → 16000, else 22050). The engine re-verifies against the
    /// voice's own config at startup and fails loudly on mismatch.
    /// </summary>
    public int? OutputSampleRate { get; set; }

    /// <summary>Piper length scale: &lt;1 faster speech, &gt;1 slower. Valid range (0, 4].</summary>
    public double LengthScale { get; set; } = 1.0;

    /// <summary>Warm piper processes per voice — the per-host concurrency is 1.</summary>
    public int MaxProcesses { get; set; } = 2;

    /// <summary>Bind from the root <c>"Voxa"</c> configuration section.</summary>
    public static PiperOptions FromConfiguration(IConfigurationSection voxaRoot)
    {
        var s = voxaRoot.GetSection(PiperDescriptors.ConfigSectionName);
        return new PiperOptions
        {
            Voice            = s["Voice"] ?? "en_US-lessac-medium",
            VoicePath        = s["VoicePath"],
            ExecutablePath   = s["ExecutablePath"],
            OutputSampleRate = s.GetValue<int?>("OutputSampleRate", null),
            LengthScale      = s.GetValue("LengthScale", 1.0),
            MaxProcesses     = s.GetValue("MaxProcesses", 2),
        };
    }
}
