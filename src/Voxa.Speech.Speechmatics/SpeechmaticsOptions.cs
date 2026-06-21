namespace Voxa.Speech.Speechmatics;

/// <summary>Configuration for the Speechmatics real-time (v2) streaming STT engine.</summary>
public sealed record SpeechmaticsOptions
{
    /// <summary>Speechmatics API key (or JWT) — sent as <c>Authorization: Bearer &lt;key&gt;</c>.</summary>
    public required string ApiKey { get; init; }

    /// <summary>Transcription language. Defaults to English.</summary>
    public string Language { get; init; } = "en";

    /// <summary>PCM sample rate of audio fed to STT (sent as <c>pcm_s16le</c>, mono).</summary>
    public int InputSampleRate { get; init; } = 16000;

    /// <summary>Real-time WebSocket base URL (region-specific). Override for self-service / on-prem.</summary>
    public string ApiBaseUrl { get; init; } = "wss://eu2.rt.speechmatics.com/v2";
}
