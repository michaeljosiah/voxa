using Voxa.Studio.ViewModels;

namespace Voxa.Studio.Tests;

/// <summary>
/// The Config "Device" picker surfaces GPU compatibility up front (VLS-002): a selected GPU device whose
/// whisper runtime isn't bundled is flagged with a clear, actionable warning before the user runs — instead
/// of only failing at session start. CPU is the unremarkable default; an unknown device is flagged too.
/// </summary>
public class ConfigDeviceCompatTests
{
    [Fact]
    public void Cpu_Device_Shows_No_Warning_Or_Note()
    {
        var vm = new ConfigViewModel(TestSupport.Services()) { SelectedWhisperDevice = "cpu" };

        Assert.True(vm.WhisperDeviceAvailable);
        Assert.False(vm.ShowWhisperDeviceWarning);
        Assert.False(vm.ShowWhisperDeviceNote);
    }

    [Fact]
    public void An_Unbundled_Gpu_Device_Shows_A_Clear_Warning()
    {
        // CUDA is never bundled in Studio, so selecting it flags a clear up-front warning naming the fix.
        var vm = new ConfigViewModel(TestSupport.Services()) { SelectedWhisperDevice = "cuda" };

        Assert.False(vm.WhisperDeviceAvailable);
        Assert.True(vm.ShowWhisperDeviceWarning);
        Assert.False(vm.ShowWhisperDeviceNote);
        Assert.Contains("Whisper.net.Runtime.Cuda", vm.WhisperDeviceStatus);
    }

    [Fact]
    public void An_Unknown_Device_Is_Flagged()
    {
        var vm = new ConfigViewModel(TestSupport.Services()) { SelectedWhisperDevice = "bogus" };

        Assert.False(vm.WhisperDeviceAvailable);
        Assert.True(vm.ShowWhisperDeviceWarning);
        Assert.Contains("Unknown device", vm.WhisperDeviceStatus);
    }
}
