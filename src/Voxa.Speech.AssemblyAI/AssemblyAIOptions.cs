namespace Voxa.Speech.AssemblyAI;

/// <summary>Configuration for the AssemblyAI Universal-Streaming (v3) STT engine.</summary>
public sealed record AssemblyAIOptions
{
    /// <summary>AssemblyAI API key — sent in the <c>Authorization</c> header.</summary>
    public required string ApiKey { get; init; }

    /// <summary>PCM sample rate of audio fed to STT (sent as <c>pcm_s16le</c>, mono).</summary>
    public int InputSampleRate { get; init; } = 16000;

    /// <summary>Request formatted (punctuated/cased) turn transcripts.</summary>
    public bool FormatTurns { get; init; } = true;

    /// <summary>WebSocket base URL. Override for a proxy / region.</summary>
    public string ApiBaseUrl { get; init; } = "wss://streaming.assemblyai.com/v3/ws";
}
