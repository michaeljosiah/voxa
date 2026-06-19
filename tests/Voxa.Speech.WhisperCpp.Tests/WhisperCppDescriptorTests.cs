using Microsoft.Extensions.Configuration;
using Voxa.Speech;
using Voxa.Speech.WhisperCpp;
using Whisper.net.LibraryLoader;

namespace Voxa.Speech.WhisperCpp.Tests;

public class WhisperCppDescriptorTests
{
    private static IConfigurationSection VoxaRoot(params (string Key, string Value)[] pairs)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => $"Voxa:{p.Key}", p => (string?)p.Value))
            .Build()
            .GetSection("Voxa");

    [Fact]
    public void Descriptor_Identity_And_Rate()
    {
        Assert.Equal("WhisperCpp", WhisperCppDescriptors.Stt.Name);
        Assert.Equal("WhisperCpp", WhisperCppDescriptors.Stt.ConfigSection);
        Assert.Equal(16000, WhisperCppDescriptors.Stt.PreferredInputSampleRate);
        Assert.Equal(16000, WhisperCppDescriptors.Stt.GetEffectiveInputSampleRate(VoxaRoot()));
    }

    [Fact]
    public void Default_Config_Validates_Keyless()
    {
        // The local tier's whole point: no credentials, and the default model is in the catalog.
        Assert.Empty(WhisperCppDescriptors.Stt.Validate(VoxaRoot()));
    }

    [Fact]
    public void Unknown_Model_Lists_The_Catalog()
    {
        var errors = WhisperCppDescriptors.Stt.Validate(VoxaRoot(("WhisperCpp:Model", "gigantic-v9")));

        var error = Assert.Single(errors);
        Assert.Contains("gigantic-v9", error);
        Assert.Contains("tiny.en", error);
        Assert.Contains("base.en", error);
        Assert.Contains("ModelPath", error);
    }

    [Fact]
    public void InputSampleRate_Override_Is_Rejected()
    {
        var errors = WhisperCppDescriptors.Stt.Validate(VoxaRoot(("WhisperCpp:InputSampleRate", "8000")));

        var error = Assert.Single(errors);
        Assert.Contains("8000", error);
        Assert.Contains("16000", error);
    }

    [Fact]
    public void Missing_Explicit_ModelPath_Is_A_Config_Error_Not_A_Download_Trigger()
    {
        var bogus = Path.Combine(Path.GetTempPath(), "voxa-tests", Guid.NewGuid().ToString("N") + ".bin");
        var errors = WhisperCppDescriptors.Stt.Validate(VoxaRoot(("WhisperCpp:ModelPath", bogus)));

        var error = Assert.Single(errors);
        Assert.Contains(bogus, error);
    }

    [Fact]
    public void Explicit_ModelPath_Bypasses_The_Catalog()
    {
        var path = Path.Combine(Path.GetTempPath(), "voxa-tests", Guid.NewGuid().ToString("N") + ".bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);
        try
        {
            // Bogus catalog name + a real explicit path = valid (bring-your-own-GGML).
            var errors = WhisperCppDescriptors.Stt.Validate(VoxaRoot(
                ("WhisperCpp:Model", "not-in-catalog"),
                ("WhisperCpp:ModelPath", path)));
            Assert.Empty(errors);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Offline_Cold_Cache_Fails_With_Provisioning_Instructions()
    {
        var emptyCache = Path.Combine(Path.GetTempPath(), "voxa-tests", Guid.NewGuid().ToString("N"));
        var errors = WhisperCppDescriptors.Stt.Validate(VoxaRoot(
            ("WhisperCpp:Model", "tiny.en"),
            ("Models:CachePath", emptyCache),
            ("Models:Offline", "true")));

        var error = Assert.Single(errors);
        Assert.Contains("tiny.en", error);
        Assert.Contains(emptyCache, error);
        Assert.Contains("huggingface.co", error);
        Assert.Contains("SHA-256", error);
    }

    [Fact]
    public void Offline_Warm_Cache_Validates()
    {
        var cacheRoot = Path.Combine(Path.GetTempPath(), "voxa-tests", Guid.NewGuid().ToString("N"));
        WhisperCppModelCatalog.TryGet("tiny.en", out var artifact);
        var cachedPath = new VoxaModelCache(new VoxaModelCacheOptions(cacheRoot, Offline: true)).PathFor(artifact);
        Directory.CreateDirectory(Path.GetDirectoryName(cachedPath)!);
        File.WriteAllBytes(cachedPath, [0]);
        try
        {
            var errors = WhisperCppDescriptors.Stt.Validate(VoxaRoot(
                ("WhisperCpp:Model", "tiny.en"),
                ("Models:CachePath", cacheRoot),
                ("Models:Offline", "true")));
            Assert.Empty(errors);
        }
        finally
        {
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public void Catalog_Entries_Declare_Plausible_Sizes_And_Pinned_Hashes()
    {
        // VLS-002 widened the upper bound: the large-v3 family runs up to ~3.1 GB (the catalog stopped
        // at ~500 MB when small was the largest). Every entry still declares a plausible size — the
        // progress logging and error messages rely on it — a 64-char SHA-256, and the pinned host.
        foreach (var name in WhisperCppModelCatalog.KnownModels)
        {
            Assert.True(WhisperCppModelCatalog.TryGet(name, out var artifact));
            Assert.InRange(artifact.SizeBytes, 10_000_000, 3_200_000_000);
            Assert.Equal(64, artifact.Sha256.Length);
            Assert.StartsWith("https://huggingface.co/ggerganov/whisper.cpp/", artifact.DownloadUrl.ToString());
        }
    }

    [Fact]
    public void Catalog_Includes_The_VLS002_Large_Models()
    {
        foreach (var name in new[]
                 { "medium", "medium.en", "medium-q5_0", "medium.en-q5_0", "large-v3", "large-v3-q5_0", "large-v3-turbo", "large-v3-turbo-q5_0" })
            Assert.True(WhisperCppModelCatalog.TryGet(name, out _), $"catalog is missing '{name}'");
    }

    [Fact]
    public void IsRuntimeFor_Accepts_Only_A_Matching_Gpu_Runtime()
    {
        // The fail-loud guard (Codex P2): an explicit GPU device must reject a CPU (or absent) loaded
        // runtime — the case where another engine already locked the process to CPU before this one.
        Assert.True(WhisperCppSttEngine.IsRuntimeFor(WhisperDevice.Cuda, RuntimeLibrary.Cuda));
        Assert.True(WhisperCppSttEngine.IsRuntimeFor(WhisperDevice.Cuda, RuntimeLibrary.Cuda12));
        Assert.True(WhisperCppSttEngine.IsRuntimeFor(WhisperDevice.Vulkan, RuntimeLibrary.Vulkan));
        Assert.False(WhisperCppSttEngine.IsRuntimeFor(WhisperDevice.Cuda, RuntimeLibrary.Cpu));
        Assert.False(WhisperCppSttEngine.IsRuntimeFor(WhisperDevice.CoreML, RuntimeLibrary.Cpu));
        Assert.False(WhisperCppSttEngine.IsRuntimeFor(WhisperDevice.Cuda, null));
    }

    [Fact]
    public void Device_Defaults_To_Cpu_And_Parses_Known_Values()
    {
        Assert.Equal(WhisperDevice.Cpu, WhisperCppOptions.FromConfiguration(VoxaRoot()).Device);
        Assert.Equal(WhisperDevice.Cuda, WhisperCppOptions.FromConfiguration(VoxaRoot(("WhisperCpp:Device", "cuda"))).Device);
        Assert.Equal(WhisperDevice.Auto, WhisperCppOptions.FromConfiguration(VoxaRoot(("WhisperCpp:Device", "Auto"))).Device);
        Assert.Equal(WhisperDevice.CoreML, WhisperCppOptions.FromConfiguration(VoxaRoot(("WhisperCpp:Device", "coreml"))).Device);
    }

    [Fact]
    public void Known_Device_Validates_Without_Probing_Hardware()
    {
        // Config validation is keyless and hardware-agnostic: an unavailable GPU surfaces at
        // StartAsync, not here. A syntactically valid device therefore passes validation.
        Assert.Empty(WhisperCppDescriptors.Stt.Validate(VoxaRoot(("WhisperCpp:Device", "cuda"))));
    }

    [Fact]
    public void Unknown_Device_Is_Rejected_With_Valid_Values()
    {
        var error = Assert.Single(WhisperCppDescriptors.Stt.Validate(VoxaRoot(("WhisperCpp:Device", "gpu"))));
        Assert.Contains("gpu", error);
        Assert.Contains("cuda", error);
        Assert.Contains("cpu", error);
    }
}
