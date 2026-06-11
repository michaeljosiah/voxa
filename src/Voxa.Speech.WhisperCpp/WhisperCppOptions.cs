using Microsoft.Extensions.Configuration;

namespace Voxa.Speech.WhisperCpp;

/// <summary>
/// Options for <see cref="WhisperCppSttEngine"/>, bound from the <c>Voxa:WhisperCpp</c> section.
/// No credentials — the local tier's whole point.
/// </summary>
public sealed class WhisperCppOptions
{
    /// <summary>
    /// Catalog model name (<c>tiny</c>, <c>tiny.en</c>, <c>base</c>, <c>base.en</c>, <c>small</c>,
    /// <c>small.en</c>, plus <c>-q5_1</c> quantized variants). Ignored when
    /// <see cref="ModelPath"/> is set.
    /// </summary>
    public string Model { get; set; } = "base.en";

    /// <summary>Explicit GGML model path — bypasses the catalog and cache (bring-your-own-GGML).</summary>
    public string? ModelPath { get; set; }

    /// <summary>BCP-47-ish language code (e.g. "en"). "auto" or empty enables language detection (slower).</summary>
    public string? Language { get; set; } = "en";

    /// <summary>Inference threads. Default: min(4, processor count).</summary>
    public int? Threads { get; set; }

    /// <summary>Whisper translate-to-English mode.</summary>
    public bool Translate { get; set; }

    /// <summary>True when language detection is active instead of a fixed language.</summary>
    public bool AutoDetectLanguage =>
        string.IsNullOrWhiteSpace(Language) || string.Equals(Language, "auto", StringComparison.OrdinalIgnoreCase);

    /// <summary>Bind from the root <c>"Voxa"</c> configuration section.</summary>
    public static WhisperCppOptions FromConfiguration(IConfigurationSection voxaRoot)
    {
        var s = voxaRoot.GetSection(WhisperCppDescriptors.ConfigSectionName);
        return new WhisperCppOptions
        {
            Model     = s["Model"] ?? "base.en",
            ModelPath = s["ModelPath"],
            Language  = s.GetValue("Language", "en"),
            Threads   = s.GetValue<int?>("Threads", null),
            Translate = s.GetValue("Translate", false),
        };
    }
}
