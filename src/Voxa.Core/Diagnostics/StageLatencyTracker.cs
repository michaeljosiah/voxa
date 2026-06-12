namespace Voxa.Diagnostics;

/// <summary>
/// Derives per-turn <see cref="StageLatencyEvent"/>s from the anchor events flowing through a
/// <see cref="VoxaDiagnosticsHub"/>, and records every stage on the <c>voxa.stage.latency</c>
/// histogram (roadmap P7: the breakdown that makes <c>voxa.turn.ttfb</c> diagnosable).
///
/// <para>
/// Stages, in turn order:
/// <c>vad_close</c> — last voiced VAD window → gate close (≈ the configured hangover);
/// <c>stt_final</c> — gate close → final transcript;
/// <c>agent_first_token</c> — final transcript → first agent delta;
/// <c>tts_first_byte</c> — first agent delta → first synthesized audio chunk.
/// A sink may publish <c>audio_out</c> itself; it flows through here only for the histogram.
/// </para>
///
/// <para>
/// Runs inside the hub's publish lock — single-threaded by construction. All timestamps come
/// from the hub's monotonic stamp on the events themselves, so latencies are renderer-independent.
/// </para>
/// </summary>
internal sealed class StageLatencyTracker
{
    private long? _lastVoicedMicros;
    private long? _turnStartMicros;   // gate close (UserStopped) — the turn's t0
    private long? _sttFinalMicros;
    private long? _agentFirstMicros;
    private long? _ttsFirstMicros;

    /// <summary>
    /// Observe one published event. Returns a derived <see cref="StageLatencyEvent"/> when the
    /// event completes a stage (the hub dispatches it and feeds it back in), else null.
    /// </summary>
    public StageLatencyEvent? Process(DiagnosticEvent e)
    {
        switch (e)
        {
            case VadWindowEvent vad:
                if (vad.Voiced) _lastVoicedMicros = vad.TimestampMicros;
                return null;

            case TurnEvent { Edge: TurnEdge.UserStopped } stop:
                _turnStartMicros = stop.TimestampMicros;
                _sttFinalMicros = _agentFirstMicros = _ttsFirstMicros = null;
                return _lastVoicedMicros is { } voiced
                    ? Stage("vad_close", stop.TimestampMicros - voiced)
                    : null;

            case TurnEvent { Edge: TurnEdge.UserStarted or TurnEdge.Interrupted }:
                // New speech (or a barge-in) abandons any half-measured turn.
                _turnStartMicros = _sttFinalMicros = _agentFirstMicros = _ttsFirstMicros = null;
                return null;

            case TranscriptEvent { IsFinal: true } t when _turnStartMicros is { } t0 && _sttFinalMicros is null:
                _sttFinalMicros = t.TimestampMicros;
                return Stage("stt_final", t.TimestampMicros - t0);

            case AgentDeltaEvent a when _sttFinalMicros is { } stt && _agentFirstMicros is null:
                _agentFirstMicros = a.TimestampMicros;
                return Stage("agent_first_token", a.TimestampMicros - stt);

            case TtsChunkEvent c when _agentFirstMicros is { } agent && _ttsFirstMicros is null:
                _ttsFirstMicros = c.TimestampMicros;
                return Stage("tts_first_byte", c.TimestampMicros - agent);

            case StageLatencyEvent stage:
                // Both tracker-derived and externally-published (e.g. a sink's audio_out) stages
                // land on the histogram here — one recording site, no double counting.
                VoxaMetrics.StageLatencyMs.Record(
                    stage.Ms, new KeyValuePair<string, object?>("stage", stage.Stage));
                return null;

            default:
                return null;
        }
    }

    private static StageLatencyEvent Stage(string stage, long deltaMicros)
        => new(stage, deltaMicros / 1000.0);
}
