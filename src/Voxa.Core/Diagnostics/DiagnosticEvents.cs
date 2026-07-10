namespace Voxa.Diagnostics;

/// <summary>
/// Base type for all pipeline diagnostics events (VST-001 WS0). Events are published to a
/// per-session <see cref="VoxaDiagnosticsHub"/> by the diagnostics taps and consumed by
/// renderers (Voxa Studio's Talk view, a host's debug page).
///
/// <para>
/// <see cref="SeqNo"/> is hub-assigned and gap-free per session, so a renderer can detect drops
/// (the hub deliberately drops oldest events rather than backpressure the pipeline).
/// <see cref="TimestampMicros"/> is stamped by the hub from a monotonic clock at publish time —
/// stage latencies are derived from it, so all events in a session share one timebase.
/// Both are hub-owned: publishers leave them at zero.
/// </para>
/// </summary>
public abstract record DiagnosticEvent
{
    /// <summary>Gap-free per-session sequence number, assigned by the hub at publish.</summary>
    public long SeqNo { get; internal set; }

    /// <summary>Monotonic microseconds since the hub was created, stamped at publish.</summary>
    public long TimestampMicros { get; internal set; }
}

/// <summary>
/// One VAD inference window (~32 ms at 16 kHz): the model's speech probability, the window's
/// RMS energy, whether this window counted as voiced (probability AND energy above their
/// thresholds — the tracker uses the last voiced window to measure the close hangover), and
/// whether the speech gate is currently open. ~31 events/s while audio flows — the densest
/// event in the stream, and the one that renders the VAD trace.
/// </summary>
public sealed record VadWindowEvent(float Probability, double Rms, bool Voiced, bool GateOpen) : DiagnosticEvent;

/// <summary>Which conversational turn boundary a <see cref="TurnEvent"/> marks.</summary>
public enum TurnEdge
{
    UserStarted,
    UserStopped,
    BotStarted,
    BotStopped,
    Interrupted,
}

/// <summary>A turn boundary observed in the pipeline (speaking-event and interruption frames).</summary>
public sealed record TurnEvent(TurnEdge Edge) : DiagnosticEvent;

/// <summary>An STT result. <c>IsFinal=false</c> for interim hypotheses.</summary>
public sealed record TranscriptEvent(string Text, bool IsFinal) : DiagnosticEvent;

/// <summary>A streamed agent (LLM) text delta.</summary>
public sealed record AgentDeltaEvent(string TextDelta) : DiagnosticEvent;

/// <summary>A synthesized audio chunk leaving the TTS stage.</summary>
public sealed record TtsChunkEvent(int Bytes, int SampleRate) : DiagnosticEvent;

/// <summary>
/// One measured stage of a turn. Stages emitted by the built-in tracker:
/// <c>vad_close</c> (last voiced window → gate close, ≈ the configured VAD hangover),
/// <c>stt_final</c> (gate close → final transcript),
/// <c>agent_first_token</c> (final transcript → first agent delta),
/// <c>tts_first_byte</c> (first agent delta → first synthesized audio).
/// Sinks may additionally publish <c>audio_out</c> (first synthesized audio → on the wire /
/// at the device). Each is also recorded on the <c>voxa.stage.latency</c> histogram with a
/// <c>stage</c> tag, which is what makes <c>voxa.turn.ttfb</c> diagnosable (roadmap P7).
/// </summary>
public sealed record StageLatencyEvent(string Stage, double Ms) : DiagnosticEvent;

/// <summary>An <c>ErrorFrame</c> observed travelling through the pipeline.</summary>
public sealed record PipelineErrorEvent(string Source, string Message) : DiagnosticEvent;

/// <summary>
/// An agent-turn boundary with its trigger kind (VDX-008 §8). The trigger distinguishes
/// background-result turns — including empty gated-to-silence ones — from user turns, so
/// dashboards don't read them as zero-output anomalies.
/// </summary>
public sealed record LlmTurnEvent(string TurnId, bool Started, Frames.TurnTrigger Trigger) : DiagnosticEvent;

/// <summary>A delegated background task began executing on a worker (VDX-008 §8).</summary>
public sealed record BackgroundTaskStartedEvent(string TaskId, string Goal) : DiagnosticEvent;

/// <summary>A delegated background task finished — success, error, or timeout (VDX-008 §8).</summary>
public sealed record BackgroundTaskCompletedEvent(string TaskId, bool IsError, double ElapsedMs) : DiagnosticEvent;

/// <summary>A delegation request was rejected because the request queue was at capacity (VDX-008 §5).</summary>
public sealed record BackgroundTaskRejectedEvent(string TaskId) : DiagnosticEvent;

/// <summary>A held background result was evicted by the pending cap — drop-oldest (VDX-008 §4.1).</summary>
public sealed record BackgroundTaskDroppedEvent(string TaskId) : DiagnosticEvent;
