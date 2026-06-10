namespace Voxa.AspNetCore;

/// <summary>
/// Root options bound from the "Voxa" configuration section.
/// Provider-specific blocks (e.g. "Voxa:OpenAI", "Voxa:ElevenLabs") are NOT modeled here —
/// each registered provider descriptor binds its own sub-section, so adding a provider never
/// requires touching this type.
/// </summary>
public sealed class VoxaOptions
{
    public const string SectionName = "Voxa";

    /// <summary>Named tuning preset: "Default", "LowLatency", "Quality", "Cheap". Case-insensitive.</summary>
    public string Profile { get; set; } = "Default";

    /// <summary>STT provider name as registered in the provider registry, e.g. "OpenAI", "Azure".</summary>
    public string? Stt { get; set; }

    /// <summary>TTS provider name, e.g. "OpenAI", "ElevenLabs", "Azure", "Mistral".</summary>
    public string? Tts { get; set; }

    public VoxaVadOptions Vad { get; set; } = new();
    public VoxaAgentOptions Agent { get; set; } = new();
    public VoxaAggregatorOptions Aggregator { get; set; } = new();
}

/// <summary>VAD selection and tuning. Null tunables fall through to the active profile.</summary>
public sealed class VoxaVadOptions
{
    /// <summary>
    /// "Silero" (default; requires the Silero descriptor registered by the Voxa meta-package),
    /// "SilenceGate" (energy-only fallback, no ONNX), or "None".
    /// </summary>
    public string Engine { get; set; } = "Silero";

    public float?  ConfidenceThreshold { get; set; }
    public double? MinRms { get; set; }
    public int?    StartDurationMs { get; set; }
    public int?    StopDurationMs { get; set; }
    public int?    PrerollDurationMs { get; set; }
}

/// <summary>
/// Default agent configuration for UseDefaults(). Ignored when the host registers its own
/// AIAgent or IChatClient in DI (DI always wins — see WS4 resolution order).
/// </summary>
public sealed class VoxaAgentOptions
{
    /// <summary>
    /// "OpenAI" is the only built-in provider (shipped by the Voxa meta-package).
    /// Null means an AIAgent or IChatClient MUST be resolvable from DI.
    /// </summary>
    public string? Provider { get; set; }

    public string Model { get; set; } = "gpt-4o-mini";
    public string Instructions { get; set; } =
        "You are a helpful voice assistant. Keep responses brief and conversational.";

    /// <summary>Falls back to "Voxa:OpenAI:ApiKey" when null and Provider == "OpenAI".</summary>
    public string? ApiKey { get; set; }

    /// <summary>Built-in per-connection chat history. Default on.</summary>
    public bool ConversationMemory { get; set; } = true;

    /// <summary>History cap: oldest user/assistant pairs are trimmed beyond this.</summary>
    public int MaxHistoryMessages { get; set; } = 50;
}

public sealed class VoxaAggregatorOptions
{
    public int? EagerFirstChunkMinChars { get; set; }
    public int? MaxBufferChars { get; set; }
}
