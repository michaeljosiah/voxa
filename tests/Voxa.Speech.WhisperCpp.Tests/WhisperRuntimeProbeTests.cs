using Voxa.Speech.WhisperCpp;

namespace Voxa.Speech.WhisperCpp.Tests;

/// <summary>
/// The safe (no-load) device-availability probe behind Studio's GPU compatibility indicator (VLS-002).
/// CPU/Auto are always available; a GPU backend reads available only if its runtime folder is deployed —
/// this package's tests bundle the CPU runtime only, so a GPU backend reads unavailable with a fix.
/// </summary>
public class WhisperRuntimeProbeTests
{
    [Fact]
    public void Cpu_And_Auto_Are_Always_Available()
    {
        Assert.True(WhisperRuntimeProbe.IsAvailable(WhisperDevice.Cpu));
        Assert.True(WhisperRuntimeProbe.IsAvailable(WhisperDevice.Auto));

        var available = WhisperRuntimeProbe.AvailableDevices();
        Assert.Contains(WhisperDevice.Cpu, available);
        Assert.Contains(WhisperDevice.Auto, available);
    }

    [Fact]
    public void An_Unbundled_Gpu_Runtime_Reads_Unavailable_With_A_Copy_Paste_Fix()
    {
        // No CUDA whisper runtime is deployed for this package's tests, so it must read unavailable on every
        // host (deterministic), and the remediation names the exact package to add.
        Assert.False(WhisperRuntimeProbe.IsAvailable(WhisperDevice.Cuda));
        Assert.DoesNotContain(WhisperDevice.Cuda, WhisperRuntimeProbe.AvailableDevices());
        Assert.Contains("Whisper.net.Runtime.Cuda", WhisperRuntimeProbe.Remediation(WhisperDevice.Cuda));
    }
}
