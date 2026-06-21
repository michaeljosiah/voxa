namespace Voxa.Speech.Gladia;

/// <summary>Configuration for the Gladia live (v2) streaming STT engine.</summary>
public sealed record GladiaOptions
{
    /// <summary>Gladia API key — sent as the <c>x-gladia-key</c> header on the session-init POST.</summary>
    public required string ApiKey { get; init; }

    /// <summary>Optional BCP-47 language hint. Null lets Gladia auto-detect.</summary>
    public string? Language { get; init; }

    /// <summary>PCM sample rate of audio fed to STT (16-bit, mono).</summary>
    public int InputSampleRate { get; init; } = 16000;

    /// <summary>HTTP base URL for the session-init call (<c>/v2/live</c> is appended).</summary>
    public string ApiBaseUrl { get; init; } = "https://api.gladia.io";
}
