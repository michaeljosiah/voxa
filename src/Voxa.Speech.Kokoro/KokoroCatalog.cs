using System.Runtime.InteropServices;

namespace Voxa.Speech.Kokoro;

/// <summary>
/// Pinned Kokoro artifact catalog (VLS-001 §6.WS3.3): the ONNX model per precision and the voice
/// style vectors from <c>onnx-community/Kokoro-82M-v1.0-ONNX</c> (Apache-2.0), plus the
/// per-platform espeak-ng CLI from the official rhasspy <c>piper-phonemize 2023.11.14-4</c>
/// release (GPL — which is exactly why it is a separate executable, never linked).
/// </summary>
public static class KokoroCatalog
{
    /// <summary>Kokoro-82M's fixed output rate — a model constant, not a tunable.</summary>
    public const int OutputSampleRate = 24000;

    /// <summary>Style vector row width; voice files are 510 rows × 256 floats.</summary>
    public const int StyleDim = 256;

    /// <summary>Max phoneme tokens per inference (512 positions minus the two boundary pads).</summary>
    public const int MaxTokens = 510;

    private const string ModelBaseUrl = "https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX/resolve/main/";
    private const string EspeakBaseUrl = "https://github.com/rhasspy/piper-phonemize/releases/download/2023.11.14-4/";

    // ── models (per precision) ──────────────────────────────────────────────

    private static readonly Dictionary<string, VoxaModelArtifact> Models = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fp32"] = new("kokoro/model.fp32.onnx", new Uri(ModelBaseUrl + "onnx/model.onnx"),
            "8fbea51ea711f2af382e88c833d9e288c6dc82ce5e98421ea61c058ce21a34cb", 325_532_232),
        ["fp16"] = new("kokoro/model.fp16.onnx", new Uri(ModelBaseUrl + "onnx/model_fp16.onnx"),
            "ba4527a874b42b21e35f468c10d326fdff3c7fc8cac1f85e9eb6c0dfc35c334a", 163_234_740),
        ["int8"] = new("kokoro/model.int8.onnx", new Uri(ModelBaseUrl + "onnx/model_quantized.onnx"),
            "fbae9257e1e05ffc727e951ef9b9c98418e6d79f1c9b6b13bd59f5c9028a1478", 92_361_116),
    };

    public static bool TryGetModel(string precision, out VoxaModelArtifact artifact)
        => Models.TryGetValue(precision, out artifact!);

    public static IReadOnlyCollection<string> KnownPrecisions { get; } =
        Models.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();

    // ── voices (style vectors, ~0.5 MB each) ────────────────────────────────

    private static VoxaModelArtifact VoiceEntry(string name, string sha256)
        => new($"kokoro/voices/{name}.bin", new Uri($"{ModelBaseUrl}voices/{name}.bin"), sha256, 522_240);

    private static readonly Dictionary<string, VoxaModelArtifact> Voices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["af_heart"]   = VoiceEntry("af_heart",   "d583ccff3cdca2f7fae535cb998ac07e9fcb90f09737b9a41fa2734ec44a8f0b"),
        ["af_bella"]   = VoiceEntry("af_bella",   "f69d836209b78eb8c66e75e3cda491e26ea838a3674257e9d4e5703cbaf55c8b"),
        ["am_michael"] = VoiceEntry("am_michael", "1d1f21dd8da39c30705cd4c75d039d265e9bc4a2a93ed09bc9e1b1225eb95ba1"),
        ["bf_emma"]    = VoiceEntry("bf_emma",    "3754352c4aaa46d17f27654ab7518d65b62ad6163a0f55a5f4330c2da2c4e94f"),
        ["bm_george"]  = VoiceEntry("bm_george",  "b8f671cef828c30e66fdf0b0756a76bba58f6bb3398cbbf27058642acbcedb97"),
    };

    public static bool TryGetVoice(string voice, out VoxaModelArtifact artifact)
        => Voices.TryGetValue(voice, out artifact!);

    public static IReadOnlyCollection<string> KnownVoices { get; } =
        Voices.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();

    // ── espeak-ng CLI (per RID, archive layout differs per platform) ────────

    private static VoxaModelArtifact EspeakEntry(string file, string entry, string sha256, long sizeBytes)
        => new($"kokoro/espeak/{file}", new Uri(EspeakBaseUrl + file), sha256, sizeBytes)
        { ArchiveEntry = entry, Executable = true };

    private static readonly Dictionary<string, VoxaModelArtifact> EspeakByRid = new(StringComparer.OrdinalIgnoreCase)
    {
        // Upstream archive root naming is inconsistent: Windows and macOS use "piper-phonemize/"
        // (hyphen), Linux tarballs use "piper_phonemize/" (underscore) — the ArchiveEntry must
        // match each exactly.
        ["win-x64"]     = EspeakEntry("piper-phonemize_windows_amd64.zip",   "piper-phonemize/bin/espeak-ng.exe", "a6f1a3f80eba222c1b8eb3904a9c18781f3c21827bd2dc36bd85b216a306d945", 61_911_468),
        ["linux-x64"]   = EspeakEntry("piper-phonemize_linux_x86_64.tar.gz", "piper_phonemize/bin/espeak-ng",     "3fb3d58b4ac42bd69d38948acdbeab335eee7e599984169d28fb0082496649ad", 25_676_144),
        ["linux-arm64"] = EspeakEntry("piper-phonemize_linux_aarch64.tar.gz","piper_phonemize/bin/espeak-ng",     "f216660f6225a165155839110cd387947d69618f014f3d1c56729fdedb6557cc", 25_224_970),
        ["osx-x64"]     = EspeakEntry("piper-phonemize_macos_x64.tar.gz",    "piper-phonemize/bin/espeak-ng",     "9ec6e300c0d012a663758bc45a097b47ee759761a3b91c7742de042af789d84b", 26_641_959),
        // NO osx-arm64: the upstream 2023.11.14-4 piper-phonemize_macos_aarch64.tar.gz ships an
        // x86_64 espeak-ng mislabeled as aarch64 (verified: Mach-O cputype 0x01000007), so it
        // fails on Apple Silicon without Rosetta. Apple Silicon is PATH/EspeakPath-only —
        // `brew install espeak-ng` provides a native arm64 binary.
    };

    public static VoxaModelArtifact? EspeakForCurrentPlatform()
        => EspeakByRid.TryGetValue(CurrentRid(), out var a) ? a : null;

    /// <summary>Look up the pinned espeak-ng artifact for a RID. Internal — for catalog tests.</summary>
    internal static bool TryGetEspeak(string rid, out VoxaModelArtifact artifact)
        => EspeakByRid.TryGetValue(rid, out artifact!);

    public static string CurrentRid()
    {
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            var other => other.ToString().ToLowerInvariant(),
        };
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        return $"{os}-{arch}";
    }

    /// <summary>Probe the <c>PATH</c> for a system-installed espeak-ng.</summary>
    public static string? FindEspeakOnPath()
    {
        var fileName = OperatingSystem.IsWindows() ? "espeak-ng.exe" : "espeak-ng";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* malformed PATH segment */ }
        }
        return null;
    }
}
