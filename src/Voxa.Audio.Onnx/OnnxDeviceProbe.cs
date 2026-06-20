using Microsoft.ML.OnnxRuntime;

namespace Voxa.Audio.Onnx;

/// <summary>
/// A pre-flight check for which <see cref="OnnxDevice"/> execution providers the loaded ONNX Runtime can use,
/// via <c>OrtEnv.GetAvailableProviders()</c> — the real list of EPs compiled into the native runtime this app
/// shipped. The base Voxa package bundles the CPU runtime only, so a GPU device reports unavailable until the
/// app adds the matching <c>Microsoft.ML.OnnxRuntime.Gpu</c>/<c>.DirectML</c> package; it then lights up
/// automatically. Returns plain strings/bools so callers (e.g. Studio) need no ONNX Runtime reference of their
/// own — the query runs here, where the runtime lives.
/// </summary>
public static class OnnxDeviceProbe
{
    /// <summary>The execution providers the loaded ONNX Runtime supports (e.g. <c>CPUExecutionProvider</c>,
    /// <c>CUDAExecutionProvider</c>, <c>DmlExecutionProvider</c>, <c>CoreMLExecutionProvider</c>).</summary>
    public static IReadOnlyList<string> AvailableProviders
    {
        get
        {
            try { return OrtEnv.Instance().GetAvailableProviders().ToList(); }
            catch { return ["CPUExecutionProvider"]; } // ORT not loadable for some reason — CPU is the safe floor
        }
    }

    /// <summary>True when the EP for <paramref name="device"/> is in the loaded runtime (CPU/Auto always true).</summary>
    public static bool IsAvailable(OnnxDevice device) => device switch
    {
        OnnxDevice.Cpu or OnnxDevice.Auto => true,
        OnnxDevice.Cuda => Has("CUDAExecutionProvider"),
        OnnxDevice.DirectML => Has("DmlExecutionProvider"),
        OnnxDevice.CoreML => Has("CoreMLExecutionProvider"),
        _ => false,
    };

    /// <summary>How to enable <paramref name="device"/> when its EP isn't in the runtime — the copy-paste fix.</summary>
    public static string Remediation(OnnxDevice device) => device switch
    {
        OnnxDevice.Cuda =>
            "No CUDA execution provider in the loaded ONNX Runtime. Add the Microsoft.ML.OnnxRuntime.Gpu package " +
            "(NVIDIA + CUDA toolkit), or use directml / cpu.",
        OnnxDevice.DirectML =>
            "No DirectML execution provider in the loaded ONNX Runtime. Add the Microsoft.ML.OnnxRuntime.DirectML " +
            "package (Windows), or use cpu.",
        OnnxDevice.CoreML =>
            "No CoreML execution provider in the loaded ONNX Runtime (Apple-only build), or use cpu.",
        _ => "Use cpu.",
    };

    private static bool Has(string executionProvider) =>
        AvailableProviders.Any(ep => string.Equals(ep, executionProvider, StringComparison.OrdinalIgnoreCase));
}
