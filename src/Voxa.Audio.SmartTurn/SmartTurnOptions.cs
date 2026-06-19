using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Voxa.Audio.SmartTurn;

/// <summary>
/// Configuration for the smart-turn classifier, bound from the <c>Voxa:SmartTurn</c> section by
/// <c>AddVoxaSmartTurn</c>.
/// </summary>
public sealed record SmartTurnOptions
{
    public const string SectionName = "SmartTurn";

    /// <summary>The endpoint that classifies recent speech as turn-complete (required for the HTTP provider).</summary>
    public string? Endpoint { get; init; }

    /// <summary>Optional bearer token, sent as the <c>Authorization</c> header.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Completion threshold applied to a probability-style response (0..1). Default 0.5.</summary>
    public double Threshold { get; init; } = 0.5;

    /// <summary>Per-request timeout in milliseconds — kept short, it sits on the turn-taking path. Default 300.</summary>
    public int TimeoutMs { get; init; } = 300;

    // ── Sidecar provider (a Voxa-managed Python process running the real model) ──

    /// <summary>Python interpreter for the sidecar provider. Default <c>python</c>.</summary>
    public string PythonExe { get; init; } = "python";

    /// <summary>Path to the sidecar script (dev mode) — <c>sidecar/voxa_smart_turn_sidecar.py</c>.</summary>
    public string? PythonScript { get; init; }

    /// <summary>Path to a frozen sidecar binary (production), used instead of PythonExe + PythonScript.</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>The Hugging Face model id the sidecar loads. Default the v3 turn-detection model.</summary>
    public string Model { get; init; } = "pipecat-ai/smart-turn-v3";

    /// <summary>
    /// Sidecar: budget for the first launch + model load (a one-time first-run download), in milliseconds.
    /// Generous because it is paid once; exceeding it fails the turn "complete" and relaunches next turn
    /// (a partial Hugging Face download resumes from cache). Default 60000.
    /// </summary>
    public int SidecarReadyTimeoutMs { get; init; } = 60000;

    /// <summary>
    /// Sidecar: per-turn inference timeout in milliseconds — separate from <see cref="TimeoutMs"/> (the HTTP
    /// budget) since local CPU inference + stdio is slower than a warmed server. Bounds a mid-session hang
    /// so it fails "complete" instead of stalling the conversation. Default 2000.
    /// </summary>
    public int SidecarTimeoutMs { get; init; } = 2000;

    /// <summary>Bind from the <c>Voxa</c> configuration section (reads its <c>SmartTurn</c> child).</summary>
    public static SmartTurnOptions FromConfiguration(IConfigurationSection voxaRoot)
    {
        var s = voxaRoot.GetSection(SectionName);
        return new SmartTurnOptions
        {
            Endpoint = s["Endpoint"],
            ApiKey = s["ApiKey"],
            Threshold = double.TryParse(s["Threshold"], NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : 0.5,
            TimeoutMs = int.TryParse(s["TimeoutMs"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) ? ms : 300,
            PythonExe = s["PythonExe"] is { Length: > 0 } py ? py : "python",
            PythonScript = s["PythonScript"],
            ExecutablePath = s["ExecutablePath"],
            Model = s["Model"] is { Length: > 0 } m ? m : "pipecat-ai/smart-turn-v3",
            SidecarReadyTimeoutMs = int.TryParse(s["SidecarReadyTimeoutMs"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rt) ? rt : 60000,
            SidecarTimeoutMs = int.TryParse(s["SidecarTimeoutMs"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var st) ? st : 2000,
        };
    }
}
