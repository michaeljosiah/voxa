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
        };
    }
}
