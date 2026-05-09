namespace Voxa.Services.OpenAIRealtime;

/// <summary>
/// Configuration for an <see cref="OpenAIRealtimeProcessor"/> session. Most fields map directly to
/// the OpenAI Realtime API's <c>session.update</c> event payload.
/// </summary>
public sealed record OpenAIRealtimeOptions
{
    /// <summary>API key for OpenAI. Sent as <c>Authorization: Bearer &lt;key&gt;</c>.</summary>
    public required string ApiKey { get; init; }

    /// <summary>
    /// WebSocket endpoint. Defaults to <c>wss://api.openai.com/v1/realtime</c> — the model is appended
    /// as a <c>?model=...</c> query parameter at connection time. Override only if you proxy through
    /// a gateway.
    /// </summary>
    public Uri Endpoint { get; init; } = new("wss://api.openai.com/v1/realtime");

    /// <summary>Model id, e.g. <c>gpt-realtime</c>, <c>gpt-realtime-mini</c>, <c>gpt-4o-realtime-preview</c>. Default <c>gpt-realtime-mini</c>.</summary>
    public string Model { get; init; } = "gpt-realtime-mini";

    /// <summary>
    /// Voice id for TTS output. OpenAI options include <c>alloy</c>, <c>ash</c>, <c>ballad</c>,
    /// <c>coral</c>, <c>echo</c>, <c>sage</c>, <c>shimmer</c>, <c>verse</c>. Default <c>alloy</c>.
    /// </summary>
    public string Voice { get; init; } = "alloy";

    /// <summary>System prompt / instructions for the model.</summary>
    public string? Instructions { get; init; }

    /// <summary>Server-side VAD / turn detection config. <c>server_vad</c> is recommended.</summary>
    public OpenAIRealtimeTurnDetection TurnDetection { get; init; } = new();

    /// <summary>Function tools exposed to the model. Implementations live elsewhere — see <see cref="Voxa.Frames.ToolCallRequestFrame"/>.</summary>
    public IReadOnlyList<OpenAIRealtimeTool> Tools { get; init; } = Array.Empty<OpenAIRealtimeTool>();

    /// <summary>Sample rate of audio sent up to the API. The Realtime API expects 24 kHz pcm16.</summary>
    public int InputSampleRate { get; init; } = 24000;

    /// <summary>Sample rate of audio received from the API. The Realtime API emits 24 kHz pcm16.</summary>
    public int OutputSampleRate { get; init; } = 24000;
}

/// <summary>Server-side voice activity detection config.</summary>
public sealed record OpenAIRealtimeTurnDetection
{
    /// <summary><c>server_vad</c> (default) or <c>none</c> for client-side turn-taking.</summary>
    public string Type { get; init; } = "server_vad";

    /// <summary>Activation probability threshold in [0..1]. Default 0.5.</summary>
    public double Threshold { get; init; } = 0.5;

    /// <summary>Audio prepended to detected speech for context. Default 300 ms.</summary>
    public int PrefixPaddingMs { get; init; } = 300;

    /// <summary>Sustained silence required to end a turn. Default 500 ms.</summary>
    public int SilenceDurationMs { get; init; } = 500;
}

/// <summary>Function tool definition. The processor emits <c>ToolCallRequestFrame</c> when the model invokes this tool.</summary>
public sealed record OpenAIRealtimeTool
{
    public required string Name { get; init; }
    public required string Description { get; init; }

    /// <summary>JSON Schema for the tool's parameters, as a raw JSON string.</summary>
    public required string ParametersJsonSchema { get; init; }
}
