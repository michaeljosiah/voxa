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

    /// <summary>
    /// Minimum gap (ms) between forwarded interim transcripts (VRT-004) — coalesces a streaming engine's partial
    /// churn on the bounded data channel (the latest partial in each window wins). Null ⇒ ~150 ms. Interims are
    /// rate-limited, never gated off; finals are never coalesced.
    /// </summary>
    public int? InterimMinIntervalMs { get; set; }

    public VoxaVadOptions Vad { get; set; } = new();
    public VoxaAecOptions Aec { get; set; } = new();
    public VoxaEnhanceOptions Enhance { get; set; } = new();
    public VoxaAgentOptions Agent { get; set; } = new();
    public VoxaBackgroundAgentOptions BackgroundAgent { get; set; } = new();
    public VoxaAggregatorOptions Aggregator { get; set; } = new();
    public VoxaDiagnosticsOptions Diagnostics { get; set; } = new();
}

/// <summary>
/// Background agent delegation tuning (VDX-008). These knobs only take effect when a host registers
/// a background driver under <see cref="VoxaBackgroundAgentOptions.ServiceKey"/> — with none
/// registered the composed pipeline has no background stage and is byte-identical to today.
/// </summary>
public sealed class VoxaBackgroundAgentOptions
{
    /// <summary>
    /// The keyed-service key the composer resolves the background <c>IAgentTurnDriver</c> from:
    /// <c>services.AddKeyedScoped&lt;IAgentTurnDriver&gt;(VoxaBackgroundAgentOptions.ServiceKey, …)</c>
    /// (or use <c>AddVoxaBackgroundAgent</c>).
    /// </summary>
    public const string ServiceKey = "voxa:background";

    /// <summary>Bounded background worker pool per session.</summary>
    public int MaxConcurrentTasks { get; set; } = 2;

    /// <summary>Waiting-request cap; excess delegations are rejected with an immediate error completion
    /// the interaction model can recover from conversationally (never silently dropped).</summary>
    public int MaxQueuedRequests { get; set; } = 8;

    /// <summary>Per-task wall-clock cap in seconds; a timed-out task completes as an error, not silence.</summary>
    public int TaskTimeoutSeconds { get; set; } = 120;

    /// <summary>Held-result cap while the user speaks, drop-oldest (VDX-008 §4.1).</summary>
    public int MaxPendingResults { get; set; } = 4;

    /// <summary>Hold results that complete mid-utterance and release them data-ordered behind the
    /// utterance's turn (VDX-008 §4.1). Off ⇒ results enqueue immediately.</summary>
    public bool HoldWhileUserSpeaking { get; set; } = true;

    /// <summary>Fallback release (ms) after stop-speaking when the utterance's final transcription
    /// never arrives.</summary>
    public int HeldResultReleaseTimeoutMs { get; set; } = 2000;
}

/// <summary>
/// Pipeline diagnostics (VST-001 WS0). When enabled, the default composer inserts
/// <c>DiagnosticsTapProcessor</c>s after each stage and wires the VAD's per-window observer,
/// feeding the per-session <c>VoxaDiagnosticsHub</c> that Voxa Studio (or a host debug page)
/// subscribes to. Disabled by default: the composed pipeline is then byte-identical to one
/// without diagnostics support.
/// </summary>
public sealed class VoxaDiagnosticsOptions
{
    /// <summary>Insert diagnostics taps at compose time. Default false (production posture).</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Per-subscriber event channel capacity. Overflow drops the oldest events (visible to the
    /// subscriber as <c>SeqNo</c> gaps) — a slow renderer never backpressures the pipeline.
    /// </summary>
    public int ChannelCapacity { get; set; } = 4096;
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

    /// <summary>
    /// Speculative ("eager") STT trigger in ms (VRT-002 WS1). Must be &lt; <see cref="StopDurationMs"/> (or the
    /// active profile's stop). Null ⇒ fall through to the profile (off in Default/Quality; on in LowLatency/Cheap).
    /// </summary>
    public int? EagerSttDelayMs { get; set; }

    /// <summary>
    /// Force-split cap in ms on a single open-gate utterance (VRT-002 WS2). Null ⇒ profile default (off in
    /// Default/Quality). Keep comfortably larger than a typical sentence so it doesn't chop natural speech.
    /// </summary>
    public int? MaxUtteranceDurationMs { get; set; }
}

/// <summary>
/// Acoustic echo cancellation (VRT-003). "None" (default) inserts no AEC stage — the composed pipeline is
/// byte-identical to today; a registered engine name inserts an <c>EchoCancellerProcessor</c> before the VAD
/// (and a far-end bot-audio tap after TTS). Requires the matching <c>Voxa.Audio.Aec.*</c> package.
/// </summary>
public sealed class VoxaAecOptions
{
    /// <summary>"None" (default) or a registered AEC engine name (e.g. "WebRtc").</summary>
    public string Engine { get; set; } = "None";
}

/// <summary>
/// Local speech enhancement / denoise (VLS-004). "None" (default) inserts no enhancer stage — the composed
/// pipeline is byte-identical to today; a registered engine name inserts an <c>AudioEnhancerProcessor</c> after
/// the AEC stage and before the VAD. Requires the matching <c>Voxa.Audio.Enhance</c> package.
/// </summary>
public sealed class VoxaEnhanceOptions
{
    /// <summary>"None" (default) or a registered enhancer engine name (e.g. "DeepFilterNet3").</summary>
    public string Engine { get; set; } = "None";
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

    /// <summary>
    /// Response-duration cap in ms for a single turn (VRT-002 WS2 §6.5). When set, the agent loop stops pumping
    /// a runaway turn's output once it reaches this wall-clock bound and closes the turn cleanly. Null ⇒ profile
    /// default (off in Default/Quality; 30 s in LowLatency/Cheap).
    /// </summary>
    public int? MaxResponseDurationMs { get; set; }
}

public sealed class VoxaAggregatorOptions
{
    public int? EagerFirstChunkMinChars { get; set; }
    public int? MaxBufferChars { get; set; }
}
