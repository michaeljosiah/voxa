using System.Runtime.InteropServices;

namespace Voxa.Speech.WhisperCpp;

/// <summary>
/// A <b>safe</b> pre-flight check for which whisper.cpp <see cref="WhisperDevice"/> backends this build can
/// actually run, without loading a model. Whisper.net deploys each GPU runtime's native libraries to
/// <c>runtimes/&lt;backend&gt;/&lt;rid&gt;/</c> next to the base CPU runtime, so the presence of that folder tells
/// us the runtime is <i>bundled</i> — the only thing a UI can know up front. (Whether the GPU itself supports the
/// backend is settled at session start, which fails loud per the device contract.) Loading a model to probe is
/// deliberately avoided: Whisper.net locks the native library process-wide on first load, so a probe-by-loading
/// would pin the whole process to whatever it tried.
/// </summary>
public static class WhisperRuntimeProbe
{
    /// <summary>
    /// True when the native runtime for <paramref name="device"/> is deployed in this build. CPU and Auto are
    /// always available (Auto falls back to CPU); a GPU backend is available only if its runtime folder is present.
    /// </summary>
    public static bool IsAvailable(WhisperDevice device) => IsAvailable(device, DefaultRuntimesRoot);

    /// <summary>Test seam: the same check against an explicit runtimes root.</summary>
    internal static bool IsAvailable(WhisperDevice device, string runtimesRoot) => device switch
    {
        WhisperDevice.Cpu or WhisperDevice.Auto => true,
        // Cuda12 also satisfies Device=cuda: WhisperCppSttEngine tries the runtime order [Cuda, Cuda12] and
        // IsRuntimeFor accepts either, so a Whisper.net.Runtime.Cuda12 build must NOT read "not bundled".
        WhisperDevice.Cuda => BackendDeployed(runtimesRoot, "cuda") || BackendDeployed(runtimesRoot, "cuda12"),
        WhisperDevice.Vulkan => BackendDeployed(runtimesRoot, "vulkan"),
        WhisperDevice.CoreML => BackendDeployed(runtimesRoot, "coreml"),
        _ => false,
    };

    /// <summary>The app's deployed native-runtime root (where Whisper.net stages its backends).</summary>
    internal static string DefaultRuntimesRoot => Path.Combine(AppContext.BaseDirectory, "runtimes");

    /// <summary>The devices whose runtime is actually present (always at least CPU + Auto).</summary>
    public static IReadOnlyList<WhisperDevice> AvailableDevices()
    {
        var available = new List<WhisperDevice> { WhisperDevice.Cpu, WhisperDevice.Auto };
        foreach (var device in new[] { WhisperDevice.Cuda, WhisperDevice.Vulkan, WhisperDevice.CoreML })
            if (IsAvailable(device)) available.Add(device);
        return available;
    }

    /// <summary>How to enable <paramref name="device"/> when its runtime isn't bundled — the copy-paste fix.</summary>
    public static string Remediation(WhisperDevice device) => device switch
    {
        WhisperDevice.Cuda =>
            "The CUDA whisper runtime isn't in this build. Add the Whisper.net.Runtime.Cuda package " +
            "(NVIDIA GPU + CUDA toolkit), or pick vulkan / cpu.",
        WhisperDevice.Vulkan =>
            "The Vulkan whisper runtime isn't in this build. Add the Whisper.net.Runtime.Vulkan package, or pick cpu.",
        WhisperDevice.CoreML =>
            "The CoreML whisper runtime isn't in this build. Add the Whisper.net.Runtime.CoreML package (Apple), or pick cpu.",
        _ => "Pick cpu.",
    };

    /// <summary>Whisper.net stages GPU runtimes at <c>runtimes/&lt;backend&gt;/&lt;rid&gt;/</c>; the folder's presence = bundled.</summary>
    private static bool BackendDeployed(string runtimesRoot, string backend)
    {
        if (CurrentRid() is not { } rid) return false;
        return Directory.Exists(Path.Combine(runtimesRoot, backend, rid));
    }

    internal static string? CurrentRid()
    {
        var os = OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsMacOS() ? "macos"
            : OperatingSystem.IsLinux() ? "linux"
            : null;
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            _ => null,
        };
        return os is null || arch is null ? null : $"{os}-{arch}";
    }
}
