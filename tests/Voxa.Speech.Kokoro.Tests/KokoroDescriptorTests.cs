using Microsoft.Extensions.Configuration;
using Voxa.Speech;
using Voxa.Speech.Kokoro;

namespace Voxa.Speech.Kokoro.Tests;

public class KokoroDescriptorTests
{
    private static IConfigurationSection VoxaRoot(params (string Key, string Value)[] pairs)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => $"Voxa:{p.Key}", p => (string?)p.Value))
            .Build()
            .GetSection("Voxa");

    [Fact]
    public void Descriptor_Identity_And_Fixed_Rate()
    {
        Assert.Equal("Kokoro", KokoroDescriptors.Tts.Name);
        Assert.Equal("Kokoro", KokoroDescriptors.Tts.ConfigSection);
        Assert.Equal(24000, KokoroDescriptors.Tts.GetEffectiveOutputSampleRate(VoxaRoot()));
        // The cloud-TTS default — swapping ElevenLabs→Kokoro leaves the envelope unchanged.
        Assert.Equal(24000, KokoroDescriptors.Tts.GetEffectiveOutputSampleRate(
            VoxaRoot(("Kokoro:Voice", "bf_emma"), ("Kokoro:Precision", "int8"))));
    }

    [Fact]
    public void Default_Config_Validates_Keyless()
        => Assert.Empty(KokoroDescriptors.Tts.Validate(VoxaRoot()));

    [Fact]
    public void Rate_Override_Is_Rejected_Like_Whispers_Input_Rate()
    {
        var errors = KokoroDescriptors.Tts.Validate(VoxaRoot(("Kokoro:OutputSampleRate", "22050")));
        var error = Assert.Single(errors);
        Assert.Contains("22050", error);
        Assert.Contains("24000", error);
    }

    [Fact]
    public void Unknown_Voice_And_Precision_List_Valid_Values()
    {
        var errors = KokoroDescriptors.Tts.Validate(VoxaRoot(
            ("Kokoro:Voice", "hal9000"),
            ("Kokoro:Precision", "fp64")));

        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.Contains("hal9000") && e.Contains("af_heart"));
        Assert.Contains(errors, e => e.Contains("fp64") && e.Contains("fp16") && e.Contains("int8"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("3.5")]
    public void Speed_Out_Of_Range_Is_Rejected(string speed)
    {
        var errors = KokoroDescriptors.Tts.Validate(VoxaRoot(("Kokoro:Speed", speed)));
        Assert.Contains(errors, e => e.Contains("Speed"));
    }

    [Theory]                            // VLS-006: the shared ONNX device vocabulary, parsed (not availability-checked) at startup.
    [InlineData("cpu")]
    [InlineData("auto")]
    [InlineData("cuda")]
    [InlineData("directml")]
    [InlineData("coreml")]
    public void Valid_Device_Spellings_Validate_Clean(string device)
        => Assert.Empty(KokoroDescriptors.Tts.Validate(VoxaRoot(("Kokoro:Device", device))));

    [Fact]
    public void Unknown_Device_Lists_The_Valid_Values()
    {
        var errors = KokoroDescriptors.Tts.Validate(VoxaRoot(("Kokoro:Device", "gpu")));
        Assert.Contains(errors, e => e.Contains("gpu") && e.Contains("cuda") && e.Contains("cpu"));
    }

    [Fact]
    public void Device_Is_Read_From_Configuration_Defaulting_Null()
    {
        Assert.Equal("cuda", KokoroOptions.FromConfiguration(VoxaRoot(("Kokoro:Device", "cuda"))).Device);
        Assert.Null(KokoroOptions.FromConfiguration(VoxaRoot()).Device); // absent ⇒ null ⇒ OnnxDevice.Cpu
    }

    [Fact]
    public void Explicit_Paths_Bypass_The_Catalog()
    {
        var dir = Path.Combine(Path.GetTempPath(), "voxa-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var model = Path.Combine(dir, "custom.onnx");
        var voice = Path.Combine(dir, "custom.bin");
        File.WriteAllBytes(model, [1]);
        File.WriteAllBytes(voice, [1]);
        try
        {
            var errors = KokoroDescriptors.Tts.Validate(VoxaRoot(
                ("Kokoro:Voice", "not-in-catalog"),
                ("Kokoro:Precision", "not-a-precision"),
                ("Kokoro:ModelPath", model),
                ("Kokoro:VoicePath", voice)));
            Assert.Empty(errors);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Offline_Cold_Cache_Reports_Model_And_Voice_With_Provisioning_Text()
    {
        var emptyCache = Path.Combine(Path.GetTempPath(), "voxa-tests", Guid.NewGuid().ToString("N"));
        var errors = KokoroDescriptors.Tts.Validate(VoxaRoot(
            ("Models:CachePath", emptyCache),
            ("Models:Offline", "true")));

        Assert.Contains(errors, e => e.Contains("fp16 model") && e.Contains("huggingface.co") && e.Contains("SHA-256"));
        Assert.Contains(errors, e => e.Contains("af_heart") && e.Contains("SHA-256"));
    }

    [Fact]
    public void Catalog_Entries_Are_Well_Formed()
    {
        foreach (var precision in KokoroCatalog.KnownPrecisions)
        {
            Assert.True(KokoroCatalog.TryGetModel(precision, out var model));
            Assert.Equal(64, model.Sha256.Length);
            Assert.InRange(model.SizeBytes, 50_000_000, 400_000_000);
        }
        foreach (var name in KokoroCatalog.KnownVoices)
        {
            Assert.True(KokoroCatalog.TryGetVoice(name, out var voice));
            Assert.Equal(64, voice.Sha256.Length);
            // 510 rows × 256 floats × 4 bytes — the engine's row indexing depends on this shape.
            Assert.Equal(510 * 256 * 4, voice.SizeBytes);
        }
    }

    [Fact]
    public void Espeak_Catalog_Omits_Broken_Macos_Arm64_And_Uses_Correct_Macos_Entry_Path()
    {
        // The upstream piper-phonemize_macos_aarch64.tar.gz ships an x86_64 espeak-ng mislabeled
        // as aarch64 — Apple Silicon resolves espeak-ng via PATH / Voxa:Kokoro:EspeakPath instead.
        Assert.False(KokoroCatalog.TryGetEspeak("osx-arm64", out _));

        // macOS archives root at "piper-phonemize/" (hyphen), NOT "piper_phonemize/" (underscore,
        // used by the Linux tarballs). An entry-path mismatch makes extraction resolution fail.
        Assert.True(KokoroCatalog.TryGetEspeak("osx-x64", out var osxX64));
        Assert.Equal("piper-phonemize/bin/espeak-ng", osxX64.ArchiveEntry);
        Assert.True(KokoroCatalog.TryGetEspeak("win-x64", out var win));
        Assert.Equal("piper-phonemize/bin/espeak-ng.exe", win.ArchiveEntry);
        Assert.True(KokoroCatalog.TryGetEspeak("linux-x64", out var linux));
        Assert.Equal("piper_phonemize/bin/espeak-ng", linux.ArchiveEntry); // Linux uses underscore
    }

    [Fact]
    public void EspeakVoice_Is_Inferred_From_The_Kokoro_Voice_Prefix()
    {
        Assert.Equal("en-us", new KokoroOptions { Voice = "af_heart" }.ResolveEspeakVoice());
        Assert.Equal("en-us", new KokoroOptions { Voice = "am_michael" }.ResolveEspeakVoice());
        Assert.Equal("en-gb", new KokoroOptions { Voice = "bf_emma" }.ResolveEspeakVoice());
        Assert.Equal("en-gb", new KokoroOptions { Voice = "bm_george" }.ResolveEspeakVoice());
        Assert.Equal("de", new KokoroOptions { Voice = "af_heart", EspeakVoice = "de" }.ResolveEspeakVoice());
    }
}

/// <summary>
/// The §3.3 GPL rule enforced mechanically (VLS-T06 acceptance criterion): the Kokoro package's
/// closure must contain no KokoroSharp and no espeak-ng native — espeak stays a separate process.
/// </summary>
public class DependencyGraphGateTests
{
    [Fact]
    public void Kokoro_Assembly_References_No_KokoroSharp_Or_Espeak()
    {
        var referenced = typeof(KokoroTtsEngine).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(referenced, a =>
            a.Name!.Contains("KokoroSharp", StringComparison.OrdinalIgnoreCase) ||
            a.Name!.Contains("espeak", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void No_Espeak_Native_Is_Copied_Into_The_Output()
    {
        // KokoroSharp's NuGet would drop espeak-ng.dll/libespeak-ng.so into consumers' output —
        // exactly the contamination this gate exists to catch.
        var files = Directory.EnumerateFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .ToArray();
        Assert.DoesNotContain(files, f =>
            f!.Contains("espeak", StringComparison.OrdinalIgnoreCase) ||
            f.Contains("KokoroSharp", StringComparison.OrdinalIgnoreCase));
    }
}
