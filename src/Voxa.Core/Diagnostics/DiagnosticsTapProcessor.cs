using Voxa.Frames;
using Voxa.Processors;

namespace Voxa.Diagnostics;

/// <summary>
/// The pipeline position a <see cref="DiagnosticsTapProcessor"/> sits at. System frames flow
/// through every processor, so each tap publishes only the event types its position "owns" —
/// otherwise one <c>UserStoppedSpeakingFrame</c> would surface once per tap it passes through.
/// </summary>
public enum DiagnosticsTapScope
{
    /// <summary>After the VAD: user turn edges, interruptions, and upstream-travelling errors.</summary>
    Vad,

    /// <summary>After the STT stage: transcripts.</summary>
    Stt,

    /// <summary>After the agent stage: streamed LLM text deltas.</summary>
    Agent,

    /// <summary>After the TTS stage: synthesized audio chunks and bot turn edges.</summary>
    Tts,
}

/// <summary>
/// Pass-through processor (the <c>TracingProcessor</c> pattern) that narrates the frames it
/// observes to a <see cref="VoxaDiagnosticsHub"/> (VST-001 WS0). Inserted by the default
/// composer after each stage when <c>Voxa:Diagnostics:Enabled</c> is true; with diagnostics
/// off, the composed pipeline is byte-identical to one without taps.
///
/// <para>
/// Every publish site is guarded by <see cref="VoxaDiagnosticsHub.HasListeners"/>, so an
/// enabled-but-unobserved session pays one bool check per frame and allocates nothing.
/// </para>
/// </summary>
public sealed class DiagnosticsTapProcessor : FrameProcessor
{
    private readonly VoxaDiagnosticsHub _hub;
    private readonly DiagnosticsTapScope _scope;

    public DiagnosticsTapProcessor(VoxaDiagnosticsHub hub, DiagnosticsTapScope scope)
        : base($"DiagnosticsTap[{scope}]")
    {
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _scope = scope;
    }

    protected override ValueTask ProcessFrameAsync(Frame frame, CancellationToken ct)
    {
        if (_hub.HasListeners)
        {
            var e = Map(frame);
            if (e is not null) _hub.Publish(e);
        }

        return PushFrameAsync(frame, ct);
    }

    private DiagnosticEvent? Map(Frame frame) => _scope switch
    {
        DiagnosticsTapScope.Vad => frame switch
        {
            UserStartedSpeakingFrame => new TurnEvent(TurnEdge.UserStarted),
            UserStoppedSpeakingFrame => new TurnEvent(TurnEdge.UserStopped),
            InterruptionFrame        => new TurnEvent(TurnEdge.Interrupted),
            ErrorFrame err           => new PipelineErrorEvent(Name, err.Message),
            _ => null,
        },
        DiagnosticsTapScope.Stt => frame switch
        {
            TranscriptionFrame t => new TranscriptEvent(t.Text, t.IsFinal),
            _ => null,
        },
        DiagnosticsTapScope.Agent => frame switch
        {
            LlmTextChunkFrame chunk => new AgentDeltaEvent(chunk.Text),
            TextFrame text          => new AgentDeltaEvent(text.Text),
            // VDX-008 §8: turn edges carry the trigger kind so background-result turns (including
            // empty gated-to-silence ones) are distinguishable from user turns.
            LlmTurnStartedFrame s   => new LlmTurnEvent(s.TurnId, Started: true, s.Trigger),
            LlmTurnEndedFrame e     => new LlmTurnEvent(e.TurnId, Started: false, e.Trigger),
            _ => null,
        },
        DiagnosticsTapScope.Tts => frame switch
        {
            // Downstream audio after TTS is synthesized bot speech; upstream/user audio never
            // reaches this position in the default chain.
            AudioRawFrame { Direction: FrameDirection.Downstream } a
                                     => new TtsChunkEvent(a.Pcm.Length, a.SampleRate),
            BotStartedSpeakingFrame  => new TurnEvent(TurnEdge.BotStarted),
            BotStoppedSpeakingFrame  => new TurnEvent(TurnEdge.BotStopped),
            _ => null,
        },
        _ => null,
    };
}
