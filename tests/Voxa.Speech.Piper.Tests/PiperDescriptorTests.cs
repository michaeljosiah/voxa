using Microsoft.Extensions.Configuration;
using Voxa.Speech;
using Voxa.Speech.Piper;

namespace Voxa.Speech.Piper.Tests;

public class PiperDescriptorTests
{
    private static IConfigurationSection VoxaRoot(params (string Key, string Value)[] pairs)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => $"Voxa:{p.Key}", p => (string?)p.Value))
            .Build()
            .GetSection("Voxa");

    [Fact]
    public void Descriptor_Identity()
    {
        Assert.Equal("Piper", PiperDescriptors.Tts.Name);
        Assert.Equal("Piper", PiperDescriptors.Tts.ConfigSection);
    }

    [Fact]
    public void Default_Config_Validates_Keyless()
        => Assert.Empty(PiperDescriptors.Tts.Validate(VoxaRoot()));

    // ── effective-rate resolution (the VDX-001 envelope rule) ───────────────

    [Theory]
    [InlineData("en_US-lessac-medium", 22050)]
    [InlineData("en_US-lessac-high", 22050)]
    [InlineData("en_US-amy-low", 16000)]
    [InlineData("some_XX-custom-x_low", 16000)]
    [InlineData("some_XX-custom-high", 22050)]
    public void Rate_Is_Inferred_From_The_Voice_Name_Suffix(string voice, int expected)
    {
        var rate = PiperDescriptors.Tts.GetEffectiveOutputSampleRate(VoxaRoot(("Piper:Voice", voice)));
        Assert.Equal(expected, rate);
    }

    [Fact]
    public void Explicit_Override_Wins_Over_Suffix_Inference()
    {
        var rate = PiperDescriptors.Tts.GetEffectiveOutputSampleRate(VoxaRoot(
            ("Piper:Voice", "en_US-amy-low"),
            ("Piper:OutputSampleRate", "22050")));
        Assert.Equal(22050, rate);
    }

    // ── validation ──────────────────────────────────────────────────────────

    [Fact]
    public void Unknown_Voice_Lists_The_Catalog()
    {
        var errors = PiperDescriptors.Tts.Validate(VoxaRoot(("Piper:Voice", "klingon-warrior-high")));
        var error = Assert.Single(errors);
        Assert.Contains("klingon-warrior-high", error);
        Assert.Contains("en_US-lessac-medium", error);
        Assert.Contains("VoicePath", error);
    }

    [Fact]
    public void Override_Contradicting_A_Catalog_Voice_Is_Rejected()
    {
        var errors = PiperDescriptors.Tts.Validate(VoxaRoot(
            ("Piper:Voice", "en_US-amy-low"),
            ("Piper:OutputSampleRate", "22050")));
        var error = Assert.Single(errors);
        Assert.Contains("22050", error);
        Assert.Contains("16000", error);
    }

    [Fact]
    public void VoicePath_Without_Rate_Override_Fails_Closed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "voxa-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var voicePath = Path.Combine(dir, "custom.onnx");
        File.WriteAllBytes(voicePath, [1]);
        File.WriteAllBytes(voicePath + ".json", [1]);
        try
        {
            var errors = PiperDescriptors.Tts.Validate(VoxaRoot(("Piper:VoicePath", voicePath)));
            var error = Assert.Single(errors);
            Assert.Contains("OutputSampleRate", error);

            // With the override the same config is valid.
            Assert.Empty(PiperDescriptors.Tts.Validate(VoxaRoot(
                ("Piper:VoicePath", voicePath),
                ("Piper:OutputSampleRate", "22050"))));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void VoicePath_Missing_Json_Sibling_Is_Reported()
    {
        var dir = Path.Combine(Path.GetTempPath(), "voxa-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var voicePath = Path.Combine(dir, "lonely.onnx");
        File.WriteAllBytes(voicePath, [1]);
        try
        {
            var errors = PiperDescriptors.Tts.Validate(VoxaRoot(
                ("Piper:VoicePath", voicePath),
                ("Piper:OutputSampleRate", "22050")));
            var error = Assert.Single(errors);
            Assert.Contains(".json", error);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("4.5")]
    public void LengthScale_Out_Of_Range_Is_Rejected(string scale)
    {
        var errors = PiperDescriptors.Tts.Validate(VoxaRoot(("Piper:LengthScale", scale)));
        Assert.Contains(errors, e => e.Contains("LengthScale"));
    }

    [Fact]
    public void Offline_Cold_Cache_Reports_Voice_And_Executable()
    {
        var emptyCache = Path.Combine(Path.GetTempPath(), "voxa-tests", Guid.NewGuid().ToString("N"));
        var errors = PiperDescriptors.Tts.Validate(VoxaRoot(
            ("Models:CachePath", emptyCache),
            ("Models:Offline", "true")));

        // Voice onnx + json missing, and (unless piper happens to be on PATH) the executable too.
        Assert.Contains(errors, e => e.Contains("en_US-lessac-medium.onnx") && e.Contains("huggingface.co"));
        Assert.Contains(errors, e => e.Contains("en_US-lessac-medium.onnx.json"));
    }

    [Fact]
    public void Catalog_Entries_Are_Well_Formed()
    {
        foreach (var name in PiperVoiceCatalog.KnownVoices)
        {
            Assert.True(PiperVoiceCatalog.TryGet(name, out var voice));
            Assert.True(voice.SampleRate is 16000 or 22050);
            Assert.Equal(PiperVoiceCatalog.InferSampleRateFromName(name), voice.SampleRate);
            Assert.Equal(64, voice.Onnx.Sha256.Length);
            Assert.Equal(64, voice.Json.Sha256.Length);
            // piper requires the json sibling in the SAME directory as the onnx.
            Assert.Equal(voice.Onnx.Id + ".json", voice.Json.Id);
        }
    }
}
