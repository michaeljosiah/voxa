using Voxa.Studio.Audio;
using Voxa.Studio.Services;
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

    // ── Kokoro ONNX device (VLS-006): the same up-front compatibility surface, via OnnxDeviceProbe ──

    [Fact]
    public void Kokoro_Cpu_Device_Shows_No_Warning_Or_Note()
    {
        var vm = new ConfigViewModel(TestSupport.Services()) { SelectedKokoroDevice = "cpu" };

        Assert.True(vm.KokoroDeviceAvailable);
        Assert.False(vm.ShowKokoroDeviceWarning);
        Assert.False(vm.ShowKokoroDeviceNote);
    }

    [Fact]
    public void Kokoro_Auto_Device_Shows_A_Note_Not_A_Warning()
    {
        var vm = new ConfigViewModel(TestSupport.Services()) { SelectedKokoroDevice = "auto" };

        Assert.True(vm.KokoroDeviceAvailable);
        Assert.False(vm.ShowKokoroDeviceWarning);
        Assert.True(vm.ShowKokoroDeviceNote);
    }

    [Fact]
    public void Kokoro_Unavailable_Gpu_Device_Shows_A_Clear_Warning()
    {
        // Deterministic by PACKAGE LAYOUT, not host hardware: DirectML is never bundled in Studio (only the CUDA
        // provider is, and that's PrivateAssets so it never reaches this test project), so DmlExecutionProvider
        // is absent on every machine — selecting it flags an up-front warning naming the fix, not a CPU fallback.
        var vm = new ConfigViewModel(TestSupport.Services()) { SelectedKokoroDevice = "directml" };

        Assert.False(vm.KokoroDeviceAvailable);
        Assert.True(vm.ShowKokoroDeviceWarning);
        Assert.False(vm.ShowKokoroDeviceNote);
        Assert.Contains("DirectML", vm.KokoroDeviceStatus);
    }

    [Fact]
    public void Kokoro_Unknown_Device_Is_Flagged()
    {
        var vm = new ConfigViewModel(TestSupport.Services()) { SelectedKokoroDevice = "gpu" };

        Assert.False(vm.KokoroDeviceAvailable);
        Assert.True(vm.ShowKokoroDeviceWarning);
        Assert.Contains("Unknown device", vm.KokoroDeviceStatus);
    }

    // ── device override emission (codex P2): cpu must beat a base-config GPU device, not silently fall back ──

    [Fact]
    public async Task Selecting_Cpu_Overrides_A_Base_Config_Gpu_Device()
    {
        var config = TestSupport.LocalConfig(null,
            ("Voxa:Stt", "WhisperCpp"), ("Voxa:Tts", "Kokoro"),
            ("Voxa:WhisperCpp:Device", "cuda"), ("Voxa:Kokoro:Device", "cuda"));
        await using var services = new StudioServices(config, new NullAudioDevice(),
            new MemorySecretsStore(), new ProviderActivationStore(TestSupport.TempActivationsPath()),
            new PipelineProfileStore(TestSupport.TempProfilesPath()));
        var vm = new ConfigViewModel(services);

        Assert.Equal("cuda", vm.SelectedWhisperDevice);   // seeded from the base config
        Assert.Equal("cuda", vm.SelectedKokoroDevice);

        vm.SelectedWhisperDevice = "cpu";                  // user turns GPU back off
        vm.SelectedKokoroDevice = "cpu";

        var pairs = vm.DraftPairs();
        Assert.Equal("cpu", pairs["Voxa:WhisperCpp:Device"]);  // explicit cpu, beating the base cuda
        Assert.Equal("cpu", pairs["Voxa:Kokoro:Device"]);
    }

    [Fact]
    public void Selecting_Cpu_With_No_Base_Device_Omits_The_Key()
    {
        var vm = new ConfigViewModel(TestSupport.Services())
        {
            SelectedStt = "WhisperCpp", SelectedTts = "Kokoro",
            SelectedWhisperDevice = "cpu", SelectedKokoroDevice = "cpu",
        };

        var pairs = vm.DraftPairs();
        Assert.False(pairs.ContainsKey("Voxa:WhisperCpp:Device"));   // minimal export — cpu is the default
        Assert.False(pairs.ContainsKey("Voxa:Kokoro:Device"));
    }
}
