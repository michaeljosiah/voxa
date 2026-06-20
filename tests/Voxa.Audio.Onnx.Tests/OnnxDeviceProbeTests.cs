namespace Voxa.Audio.Onnx.Tests;

/// <summary>
/// The ONNX execution-provider availability probe behind Studio's GPU compatibility answer (VLS-006): it
/// reads OrtEnv.GetAvailableProviders(). The base package ships the CPU runtime only, so CPU/Auto are
/// available and the GPU EPs are not — each with a copy-paste remediation.
/// </summary>
public class OnnxDeviceProbeTests
{
    [Fact]
    public void Cpu_And_Auto_Are_Always_Available_And_The_Cpu_Ep_Is_Present()
    {
        Assert.True(OnnxDeviceProbe.IsAvailable(OnnxDevice.Cpu));
        Assert.True(OnnxDeviceProbe.IsAvailable(OnnxDevice.Auto));
        Assert.Contains("CPUExecutionProvider", OnnxDeviceProbe.AvailableProviders);
    }

    [Fact]
    public void The_Cpu_Runtime_Reports_No_Gpu_Provider_With_A_Fix()
    {
        // The CPU-only ORT has no CUDA/DirectML EP, so a GPU device reads unavailable and points at the package.
        Assert.False(OnnxDeviceProbe.IsAvailable(OnnxDevice.Cuda));
        Assert.False(OnnxDeviceProbe.IsAvailable(OnnxDevice.DirectML));
        Assert.Contains("Microsoft.ML.OnnxRuntime.Gpu", OnnxDeviceProbe.Remediation(OnnxDevice.Cuda));
        Assert.Contains("Microsoft.ML.OnnxRuntime.DirectML", OnnxDeviceProbe.Remediation(OnnxDevice.DirectML));
    }
}
