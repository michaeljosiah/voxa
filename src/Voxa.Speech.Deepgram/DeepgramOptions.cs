namespace Voxa.Speech.Deepgram;

/// <summary>Configuration for the Deepgram streaming STT engine.</summary>
public sealed record DeepgramOptions
{
    /// <summary>Deepgram API key — sent as <c>Authorization: Token &lt;key&gt;</c>.</summary>
    public required string ApiKey { get; init; }

    /// <summary>Model id. Defaults to <c>nova-3</c> (also <c>nova-2</c>, <c>nova-2-phonecall</c>, …).</summary>
    public string Model { get; init; } = "nova-3";

    /// <summary>Optional BCP-47 language hint. Null uses the model default / auto-detect.</summary>
    public string? Language { get; init; }

    /// <summary>PCM sample rate of audio fed to STT (sent as <c>linear16</c>, mono).</summary>
    public int InputSampleRate { get; init; } = 16000;

    /// <summary>WebSocket base URL. Override for a proxy / on-prem deployment.</summary>
    public string ApiBaseUrl { get; init; } = "wss://api.deepgram.com/v1/listen";
}
