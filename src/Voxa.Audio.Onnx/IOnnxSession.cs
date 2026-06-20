using Microsoft.ML.OnnxRuntime;

namespace Voxa.Audio.Onnx;

/// <summary>
/// A handle over one loaded ONNX model (VLS-006 WS1). Thin and test-fakeable: an engine that binds
/// <see cref="OrtValue"/>s directly over reused buffers uses <see cref="Session"/> (the zero-allocation
/// steady-state pattern <c>SileroVadEngine</c> uses today); <see cref="InputNames"/> /
/// <see cref="OutputNames"/> save a metadata round-trip. The weights are shared process-wide — never
/// dispose <see cref="Session"/> from a per-connection scope (see <see cref="OnnxModelHost"/>).
/// </summary>
public interface IOnnxSession
{
    /// <summary>
    /// The underlying ORT session. <see cref="InferenceSession.Run(RunOptions, IReadOnlyCollection{string}, IReadOnlyCollection{OrtValue}, IReadOnlyCollection{string}, IReadOnlyCollection{OrtValue})"/>
    /// is thread-safe, but a model with reused per-instance buffers is not — that contract stays with the engine.
    /// </summary>
    InferenceSession Session { get; }

    /// <summary>The model's input names, in declaration order.</summary>
    IReadOnlyList<string> InputNames { get; }

    /// <summary>The model's output names, in declaration order.</summary>
    IReadOnlyList<string> OutputNames { get; }

    /// <summary>The EP that actually loaded — may differ from the request under <see cref="OnnxDevice.Auto"/>.</summary>
    OnnxDevice ActiveDevice { get; }
}
