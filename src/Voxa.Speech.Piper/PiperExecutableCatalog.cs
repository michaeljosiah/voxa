using System.Runtime.InteropServices;

namespace Voxa.Speech.Piper;

/// <summary>
/// Pinned piper executable catalog, per RID, from the official rhasspy/piper
/// <c>2023.11.14-2</c> release. The whole archive is extracted (piper needs its sibling
/// <c>espeak-ng-data</c> and libraries) and resolution returns the executable entry.
/// Hashes computed from the published release assets.
/// </summary>
public static class PiperExecutableCatalog
{
    private const string BaseUrl = "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/";

    private static VoxaModelArtifact Entry(string file, string entry, string sha256, long sizeBytes)
        => new($"piper/bin/{file}", new Uri(BaseUrl + file), sha256, sizeBytes)
        { ArchiveEntry = entry, Executable = true };

    private static readonly Dictionary<string, VoxaModelArtifact> ByRid = new(StringComparer.OrdinalIgnoreCase)
    {
        ["win-x64"]     = Entry("piper_windows_amd64.zip",   "piper/piper.exe", "f3c58906402b24f3a96d92145f58acba6d86c9b5db896d207f78dc80811efcea", 22_477_236),
        ["linux-x64"]   = Entry("piper_linux_x86_64.tar.gz", "piper/piper",     "a50cb45f355b7af1f6d758c1b360717877ba0a398cc8cbe6d2a7a3a26e225992", 26_460_462),
        ["linux-arm64"] = Entry("piper_linux_aarch64.tar.gz","piper/piper",     "fea0fd2d87c54dbc7078d0f878289f404bd4d6eea6e7444a77835d1537ab88eb", 26_004_717),
        ["osx-x64"]     = Entry("piper_macos_x64.tar.gz",    "piper/piper",     "ced85c0a3df13945b1e623b878a48fdc2854d5c485b4b67f62857cf551deaf8b", 19_146_927),
        ["osx-arm64"]   = Entry("piper_macos_aarch64.tar.gz","piper/piper",     "6b1eb03b3735946cb35216e063e7eebcc33a6bbf5dd96ec0217959bf1cdcb0cc", 19_146_957),
    };

    /// <summary>The catalog artifact for the current OS/architecture, or null when unsupported.</summary>
    public static VoxaModelArtifact? ForCurrentPlatform() => TryGet(CurrentRid(), out var a) ? a : null;

    public static bool TryGet(string rid, out VoxaModelArtifact artifact) => ByRid.TryGetValue(rid, out artifact!);

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

    /// <summary>
    /// Probe the <c>PATH</c> for a piper executable — the escape hatch for system-installed piper
    /// and for platforms without a catalog entry.
    /// </summary>
    public static string? FindOnPath()
    {
        var fileName = OperatingSystem.IsWindows() ? "piper.exe" : "piper";
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
