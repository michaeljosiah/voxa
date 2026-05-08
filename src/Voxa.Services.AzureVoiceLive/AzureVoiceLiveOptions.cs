namespace Voxa.Services.AzureVoiceLive;

/// <summary>
/// Configuration for an <see cref="AzureVoiceLiveProcessor"/> session. Most fields map directly to
/// Voice Live's <c>session.update</c> event payload.
/// </summary>
public sealed record AzureVoiceLiveOptions
{
    /// <summary>WebSocket endpoint, e.g. <c>wss://&lt;resource&gt;.cognitiveservices.azure.com/voice-live/realtime?model=...&amp;api-version=...</c>.</summary>
    public required Uri Endpoint { get; init; }

    /// <summary>API key sent in the <c>api-key</c> header. Use Azure AD elsewhere if you need bearer auth.</summary>
    public required string ApiKey { get; init; }

    /// <summary>Model id, e.g. <c>gpt-realtime-mini</c>, <c>gpt-realtime</c>, <c>phi4-mm-realtime</c>.</summary>
    public required string Model { get; init; }

    /// <summary>Voice id for TTS output. Provider-specific. Defaults to a sensible Voice Live voice if null.</summary>
    public string? Voice { get; init; }

    /// <summary>System prompt / instructions for the model.</summary>
    public string? Instructions { get; init; }

    /// <summary>Server-side VAD / turn detection config.</summary>
    public AzureVoiceLiveTurnDetection TurnDetection { get; init; } = new();

    /// <summary>Function tools exposed to the model. Implementations live elsewhere — see <see cref="Voxa.Frames.ToolCallRequestFrame"/>.</summary>
    public IReadOnlyList<AzureVoiceLiveTool> Tools { get; init; } = Array.Empty<AzureVoiceLiveTool>();

    /// <summary>Sample rate of audio sent up to Voice Live. Voice Live currently expects 24 kHz.</summary>
    public int InputSampleRate { get; init; } = 24000;

    /// <summary>Sample rate of audio received from Voice Live. Voice Live currently emits 24 kHz.</summary>
    public int OutputSampleRate { get; init; } = 24000;
}

/// <summary>Server-side voice activity detection config.</summary>
public sealed record AzureVoiceLiveTurnDetection
{
    public string Type { get; init; } = "server_vad";
    public double Threshold { get; init; } = 0.5;
    public int PrefixPaddingMs { get; init; } = 300;
    public int SilenceDurationMs { get; init; } = 500;
}

/// <summary>Function tool definition. The processor emits <c>ToolCallRequestFrame</c> when the model invokes this tool.</summary>
public sealed record AzureVoiceLiveTool
{
    public required string Name { get; init; }
    public required string Description { get; init; }

    /// <summary>JSON Schema for the tool's parameters, as a raw JSON string.</summary>
    public required string ParametersJsonSchema { get; init; }
}
