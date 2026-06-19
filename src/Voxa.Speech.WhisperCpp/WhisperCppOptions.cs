using Microsoft.Extensions.Configuration;

namespace Voxa.Speech.WhisperCpp;

/// <summary>
/// Options for <see cref="WhisperCppSttEngine"/>, bound from the <c>Voxa:WhisperCpp</c> section.
/// No credentials — the local tier's whole point.
/// </summary>
public sealed class WhisperCppOptions
{
    /// <summary>
    /// Catalog model name: <c>tiny</c>, <c>tiny.en</c>, <c>base</c>, <c>base.en</c>, <c>small</c>,
    /// <c>small.en</c> (with <c>-q5_1</c> quantized variants), plus the VLS-002 large families
    /// <c>medium</c>, <c>medium.en</c>, <c>large-v3</c>, <c>large-v3-turbo</c> (with <c>-q5_0</c>
    /// variants). Large/medium models want a GPU — see <see cref="Device"/>. Ignored when
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

    /// <summary>
    /// Inference backend (VLS-002). <see cref="WhisperDevice.Cpu"/> (default) needs no extra packages
    /// and is what CI uses; the GPU values require the matching <c>Whisper.net.Runtime.*</c> package to
    /// be added by the host app — Voxa never bundles GPU natives.
    /// </summary>
    public WhisperDevice Device { get; set; } = WhisperDevice.Cpu;

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
            Device    = TryParseDevice(s["Device"], out var device) ? device : WhisperDevice.Cpu,
        };
    }

    /// <summary>
    /// Parse a <c>Voxa:WhisperCpp:Device</c> string (case-insensitive). Empty/absent ⇒
    /// <see cref="WhisperDevice.Cpu"/>. Numeric strings are rejected so a stray "1" can't silently
    /// select a backend. Used by both <see cref="FromConfiguration"/> and the descriptor's validation.
    /// </summary>
    public static bool TryParseDevice(string? value, out WhisperDevice device)
    {
        device = WhisperDevice.Cpu;
        if (string.IsNullOrWhiteSpace(value)) return true;
        return !int.TryParse(value, out _)
            && Enum.TryParse(value, ignoreCase: true, out device)
            && Enum.IsDefined(device);
    }
}

/// <summary>whisper.cpp inference backend (VLS-002). See <see cref="WhisperCppOptions.Device"/>.</summary>
public enum WhisperDevice
{
    /// <summary>CPU only — deterministic, no extra packages. Default.</summary>
    Cpu,
    /// <summary>Best available accelerator, falling back to CPU.</summary>
    Auto,
    /// <summary>Require an NVIDIA CUDA runtime; fail at start if it can't load.</summary>
    Cuda,
    /// <summary>Require a Vulkan runtime (cross-vendor); fail at start if it can't load.</summary>
    Vulkan,
    /// <summary>Require the Apple CoreML runtime; fail at start if it can't load (best-effort — see the VLS-002 spec).</summary>
    CoreML,
}
