using Microsoft.Extensions.Configuration;

namespace Voxa.Speech.Sidecar;

/// <summary>
/// Options for <see cref="SidecarTtsEngine"/>, bound from <c>Voxa:Sidecar</c> (VVL-002). The heavy
/// expressive/cloning models run in a separate process, so this configures how to launch it. VVL-002
/// ships no pinned frozen binary yet: set <see cref="ExecutablePath"/> to one you built, or
/// <see cref="PythonScript"/> (+ <see cref="PythonExe"/>) to run <c>sidecar/voxa_tts_sidecar.py</c>
/// directly in development. See the package README.
/// </summary>
public sealed class SidecarOptions
{
    /// <summary>Path to a frozen sidecar binary (e.g. a PyInstaller build). Wins over <see cref="PythonScript"/>.</summary>
    public string? ExecutablePath { get; set; }

    /// <summary>Dev mode: run this Python script with <see cref="PythonExe"/> instead of a frozen binary.</summary>
    public string? PythonScript { get; set; }

    /// <summary>Python interpreter for <see cref="PythonScript"/> dev mode. Default <c>python</c>.</summary>
    public string PythonExe { get; set; } = "python";

    /// <summary>Voice id, or a path to a reference clip for zero-shot cloning (engine-dependent).</summary>
    public string Voice { get; set; } = "default";

    /// <summary>BCP-47-ish language hint passed to the engine. Default <c>en</c>.</summary>
    public string? Language { get; set; } = "en";

    /// <summary>Model the sidecar should load (e.g. <c>xtts-v2</c>). Passed as a launch argument.</summary>
    public string Model { get; set; } = "xtts-v2";

    /// <summary>PCM sample rate the sidecar emits. Model-dependent; XTTS-v2 is 24 kHz.</summary>
    public int OutputSampleRate { get; set; } = 24000;

    /// <summary>Bind from the root <c>"Voxa"</c> configuration section.</summary>
    public static SidecarOptions FromConfiguration(IConfigurationSection voxaRoot)
    {
        var s = voxaRoot.GetSection(SidecarDescriptors.ConfigSectionName);
        return new SidecarOptions
        {
            ExecutablePath   = s["ExecutablePath"],
            PythonScript     = s["PythonScript"],
            PythonExe        = s["PythonExe"] ?? "python",
            Voice            = s["Voice"] ?? "default",
            Language         = s.GetValue("Language", "en"),
            Model            = s["Model"] ?? "xtts-v2",
            OutputSampleRate = s.GetValue("OutputSampleRate", 24000),
        };
    }
}
