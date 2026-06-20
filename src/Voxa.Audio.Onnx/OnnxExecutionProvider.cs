using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Voxa.Speech;

namespace Voxa.Audio.Onnx;

/// <summary>
/// Applies the execution provider for a requested <see cref="OnnxDevice"/> to a fresh
/// <see cref="SessionOptions"/> before the (cached) session is built (VLS-006 WS2). CPU is implicit; the
/// GPU appenders drive whatever <c>Microsoft.ML.OnnxRuntime.*</c> runtime the consuming app added — the
/// base Voxa package bundles only the CPU runtime, so on a CPU-only host an explicit GPU device fails with
/// a copy-pasteable remediation and <see cref="OnnxDevice.Auto"/> falls back to CPU with a warning.
/// </summary>
internal static class OnnxExecutionProvider
{
    /// <summary>
    /// Append the EP for <paramref name="device"/> onto <paramref name="options"/> and return the provider
    /// that actually loaded.
    /// </summary>
    /// <exception cref="VoxaModelUnavailableException">
    /// An explicit GPU device (<see cref="OnnxDevice.Cuda"/> / <see cref="OnnxDevice.DirectML"/> /
    /// <see cref="OnnxDevice.CoreML"/>) whose runtime isn't loaded. Never thrown for
    /// <see cref="OnnxDevice.Cpu"/> or <see cref="OnnxDevice.Auto"/>.
    /// </exception>
    public static OnnxDevice Apply(SessionOptions options, OnnxDevice device, ILogger logger)
    {
        switch (device)
        {
            case OnnxDevice.Cpu:
                return OnnxDevice.Cpu; // CPU EP is implicit; nothing to append.

            case OnnxDevice.Cuda:
            case OnnxDevice.DirectML:
            case OnnxDevice.CoreML:
                try
                {
                    Append(options, device);
                    return device;
                }
                catch (Exception ex)
                {
                    // The typed appenders fail several ways when the matching native runtime isn't present
                    // (EntryPointNotFoundException / DllNotFoundException / OnnxRuntimeException; or
                    // NotSupportedException for CoreML off-Apple). Any of them means "this EP can't load":
                    // surface the fix, never silently downgrade an explicit GPU request to CPU.
                    throw new VoxaModelUnavailableException(Remediation(device), ex);
                }

            case OnnxDevice.Auto:
                // Probe GPU EPs in priority order; the first that appends wins. A failed append throws
                // before mutating native state, so probing the next EP on the same options is safe.
                foreach (var ep in new[] { OnnxDevice.Cuda, OnnxDevice.DirectML, OnnxDevice.CoreML })
                {
                    try
                    {
                        Append(options, ep);
                        logger.LogInformation("Onnx host: Device=auto selected the {Ep} execution provider.", ep);
                        return ep;
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug("Onnx host: Device=auto — {Ep} EP unavailable ({Reason}).", ep, ex.Message);
                    }
                }
                logger.LogWarning("Onnx host: Device=auto found no GPU execution provider; using CPU.");
                return OnnxDevice.Cpu;

            default:
                return OnnxDevice.Cpu;
        }
    }

    // The typed appenders compile against the base CPU package (only #if __MOBILE__ excludes CUDA/DML);
    // they throw at runtime when the loaded native runtime lacks the provider, and CoreML's managed method
    // throws NotSupportedException off-Apple. We never reference a GPU-only symbol, so the base host stays
    // CPU-only and byte-identical — the GPU path lights up only when the app adds the matching ORT package.
    private static void Append(SessionOptions options, OnnxDevice device)
    {
        switch (device)
        {
            case OnnxDevice.Cuda: options.AppendExecutionProvider_CUDA(); break;
            case OnnxDevice.DirectML: options.AppendExecutionProvider_DML(); break;
            case OnnxDevice.CoreML: options.AppendExecutionProvider_CoreML(); break;
        }
    }

    private static string Remediation(OnnxDevice device) => device switch
    {
        OnnxDevice.Cuda =>
            "Voxa ONNX Device=cuda but no CUDA execution provider loaded. Add the " +
            "Microsoft.ML.OnnxRuntime.Gpu (CUDA) package to your app and ensure the CUDA toolkit/driver " +
            "is installed, or use Device=cpu.",
        OnnxDevice.DirectML =>
            "Voxa ONNX Device=directml but no DirectML execution provider loaded. Add the " +
            "Microsoft.ML.OnnxRuntime.DirectML package to your app (Windows), or use Device=cpu.",
        OnnxDevice.CoreML =>
            "Voxa ONNX Device=coreml but no CoreML execution provider loaded. CoreML is available only on " +
            "Apple platforms with a CoreML-enabled ONNX Runtime build, or use Device=cpu.",
        _ => $"Voxa ONNX Device={device} could not load its execution provider; use Device=cpu.",
    };
}
