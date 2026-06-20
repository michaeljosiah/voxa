namespace Voxa.Audio.Onnx;

/// <summary>
/// ONNX Runtime execution target (VLS-006 WS2). Generalises whisper.cpp's <c>WhisperDevice</c> beyond
/// Whisper so every ONNX-backed engine reads its <c>Device</c> the same way. <see cref="Cpu"/> is the
/// default and the only target the base package's bundled runtime supports out of the box — the GPU
/// providers require the consuming app to add the matching <c>Microsoft.ML.OnnxRuntime.*</c> package
/// (never bundled by Voxa).
/// </summary>
public enum OnnxDevice
{
    /// <summary>CPU EP (default). Deterministic; what CI runs and the base package supports out of the box.</summary>
    Cpu,

    /// <summary>Prefer the best available GPU EP, fall back to CPU with a warning — never a hard failure.</summary>
    Auto,

    /// <summary>Require the CUDA EP. Unavailable ⇒ fail at session creation with remediation (don't silently run on CPU).</summary>
    Cuda,

    /// <summary>Require the DirectML EP (cross-vendor on Windows: NVIDIA / AMD / Intel).</summary>
    DirectML,

    /// <summary>Require the CoreML EP (Apple Silicon). Best-effort; some models only partially offload.</summary>
    CoreML,
}

/// <summary>
/// Parses the shared <c>Device</c> config convention (VLS-006 §8) into an <see cref="OnnxDevice"/>.
/// Every ONNX-backed engine exposes a <c>Device</c> key under its own section and parses it through here,
/// so the spelling and rules are identical across models — the analogue of whisper.cpp's
/// <c>WhisperCppOptions.TryParseDevice</c>.
/// </summary>
public static class OnnxDeviceParser
{
    /// <summary>
    /// Parse a <c>Device</c> string (case-insensitive, surrounding whitespace ignored). Empty/absent ⇒
    /// <see cref="OnnxDevice.Cpu"/>. Numeric strings are rejected so a stray <c>"0"</c> can't silently be
    /// taken as a device index. <c>"dml"</c> is accepted as an alias for <see cref="OnnxDevice.DirectML"/>.
    /// </summary>
    public static bool TryParse(string? value, out OnnxDevice device)
    {
        device = OnnxDevice.Cpu;
        if (string.IsNullOrWhiteSpace(value)) return true;

        switch (value.Trim().ToLowerInvariant())
        {
            case "cpu": device = OnnxDevice.Cpu; return true;
            case "auto": device = OnnxDevice.Auto; return true;
            case "cuda": device = OnnxDevice.Cuda; return true;
            case "directml":
            case "dml": device = OnnxDevice.DirectML; return true;
            case "coreml": device = OnnxDevice.CoreML; return true;
            default: return false;
        }
    }

    /// <summary>The valid <c>Device</c> spellings, for validators and error messages.</summary>
    public static IReadOnlyList<string> ValidValues { get; } =
        ["cpu", "auto", "cuda", "directml", "coreml"];
}
