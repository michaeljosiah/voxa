using System.Text.Json.Serialization;

namespace Voxa.TurnTaking;

/// <summary>Provenance of a run — which engine backed each stage (mock by default).</summary>
public sealed record EngineNames(
    [property: JsonPropertyName("stt")] string Stt,
    [property: JsonPropertyName("llm")] string Llm,
    [property: JsonPropertyName("tts")] string Tts);

/// <summary>Per-sample timings in milliseconds, reduced from the diagnostics hub. <c>null</c> when the
/// stage didn't occur (e.g. the bot correctly stayed silent through a pause).</summary>
public sealed record SampleTimings(
    [property: JsonPropertyName("stt")] double? Stt,
    [property: JsonPropertyName("llm")] double? Llm,
    [property: JsonPropertyName("tts")] double? Tts,
    [property: JsonPropertyName("ttft_first_audio_from_speech_end")] double? TtftFirstAudioFromSpeechEnd,
    [property: JsonPropertyName("total_wall")] double? TotalWall,
    // Barge-in yield: a user-interruption while the bot is speaking → the bot's stop/interrupt. The
    // user_interruption score's real metric. Null when no barge-in occurred (the offline file-driven
    // harness has no real-time overlap, so this populates only on a real-time/duplex source).
    [property: JsonPropertyName("barge_in_yield_ms")] double? BargeInYieldMs);

/// <summary>Reference (from sample metadata) vs the STT hypothesis transcript.</summary>
public sealed record SampleTranscripts(
    [property: JsonPropertyName("reference")] string? Reference,
    [property: JsonPropertyName("hypothesis")] string? Hypothesis);

/// <summary>
/// Turn-taking signals reduced from the diagnostics hub, for the direction-aware scorer. The key one is
/// <c>UserStoppedEdges</c>: more than one on a <c>pause_handling</c> sample means the VAD ended the turn
/// during the within-turn pause (a premature turn-take — exactly what smart-turn / a longer StopDuration
/// is meant to prevent). (An extension beyond VRT-001 §6's illustrative schema; the scorer needs it.)
/// </summary>
public sealed record TurnSignals(
    [property: JsonPropertyName("user_stopped_edges")] int UserStoppedEdges,
    [property: JsonPropertyName("bot_started_edges")] int BotStartedEdges);

/// <summary>One per-sample record, serialized to <c>&lt;category&gt;__&lt;sample-id&gt;.json</c> (the FDB convention).</summary>
public sealed record SampleRecord(
    [property: JsonPropertyName("sample_id")] string SampleId,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("engines")] EngineNames Engines,
    [property: JsonPropertyName("timings_ms")] SampleTimings TimingsMs,
    [property: JsonPropertyName("transcripts")] SampleTranscripts Transcripts,
    [property: JsonPropertyName("signals")] TurnSignals Signals,
    [property: JsonPropertyName("response_wav")] string? ResponseWav,
    [property: JsonPropertyName("error")] string? Error);
