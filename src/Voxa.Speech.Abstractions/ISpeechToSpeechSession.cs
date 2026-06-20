namespace Voxa.Speech;

/// <summary>
/// A single local (or cloud) full-duplex speech-to-speech session (VRT-005 WS1): user audio in, agent audio +
/// text out, at the model's own frame rate. Models speech-core's <c>FullDuplexSpeechInterface</c>, and is shaped
/// deliberately parallel to what the cloud realtime processors (OpenAI Realtime / Azure Voice Live) already do
/// internally — push user PCM, read back assistant audio + text + speaking events, cancel on barge-in — so the
/// driving <c>SpeechToSpeechProcessor</c> is a genuine third member of that composite family.
///
/// <para>Session-scoped (one per connection / turn context, like a KV-cache session) and streaming. The concrete
/// implementation — a real local model on the VLS-006 host, a GPU/sidecar host, or a cloud S2S provider — is
/// deferred (VRT-005 WS3); this seam ships so the composite can be built and tested against a fake session now.</para>
/// </summary>
public interface ISpeechToSpeechSession : IAsyncDisposable
{
    /// <summary>
    /// Sample rate (Hz) of the agent audio this session emits, e.g. 24000. The composite advertises it on the
    /// session envelope and tags every outbound <c>AudioRawFrame</c> with it, exactly like the cloud composites.
    /// </summary>
    int OutputSampleRate { get; }

    /// <summary>
    /// Push a chunk of user PCM (16-bit mono) into the model's full-duplex loop. Non-blocking; agent output
    /// arrives out-of-band via <see cref="RespondAsync"/>. Full-duplex: the user may speak while the agent is
    /// speaking — the model owns overlap / barge-in detection.
    /// </summary>
    ValueTask AppendUserAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct);

    /// <summary>
    /// The agent's output + session-event stream, mirroring <c>FullDuplexChunk</c> plus the speaking-edge events
    /// the realtime transports surface. Yields for the lifetime of the session and honours the per-frame
    /// <see cref="CancellationToken"/> so an interruption aborts in-flight synthesis.
    /// </summary>
    IAsyncEnumerable<SpeechToSpeechChunk> RespondAsync(CancellationToken ct);

    /// <summary>Preset / voice selection (<c>FullDuplexSpeechInterface.set_voice</c>).</summary>
    ValueTask SetVoiceAsync(string voiceId, CancellationToken ct);

    /// <summary>System prompt / persona (<c>set_system_prompt</c>).</summary>
    ValueTask SetSystemPromptAsync(string systemPrompt, CancellationToken ct);

    /// <summary>Clear conversational state / KV-cache for a fresh turn (<c>reset_session</c>).</summary>
    ValueTask ResetSessionAsync(CancellationToken ct);

    /// <summary>
    /// Abort the in-flight response (barge-in) without tearing down the session. The composite calls this from
    /// its interruption hook when an <c>InterruptionFrame</c> reaches it.
    /// </summary>
    ValueTask CancelAsync(CancellationToken ct);
}

/// <summary>
/// A session event riding alongside agent output in <see cref="SpeechToSpeechChunk"/>, so the model's
/// full-duplex VAD / barge-in surfaces the same speaking-edge frames the cloud composites emit. The seam keeps
/// a single output stream (like a realtime transport's one event stream) rather than a second channel.
/// </summary>
public enum SpeechToSpeechEvent
{
    /// <summary>A plain agent-output chunk (audio and/or text); no session event.</summary>
    None,

    /// <summary>The model's VAD confirmed the user started speaking → <c>UserStartedSpeakingFrame</c>.</summary>
    UserStartedSpeaking,

    /// <summary>The model's VAD confirmed the user stopped speaking → <c>UserStoppedSpeakingFrame</c>.</summary>
    UserStoppedSpeaking,

    /// <summary>The model aborted its response on barge-in → <c>InterruptionFrame</c> (downstream purges queued bot audio).</summary>
    Interrupted,
}

/// <summary>
/// One unit of agent output (mirrors <c>FullDuplexChunk</c>): agent audio PCM at the session's
/// <see cref="ISpeechToSpeechSession.OutputSampleRate"/>, optional decoded text token(s), and an end-of-turn
/// marker — or, when <see cref="Event"/> is not <see cref="SpeechToSpeechEvent.None"/>, a session event (its
/// audio/text are then typically empty). Either output field may be empty in a given chunk.
/// </summary>
public readonly record struct SpeechToSpeechChunk(
    ReadOnlyMemory<byte> AudioPcm,
    string? Text,
    bool IsFinal,
    SpeechToSpeechEvent Event = SpeechToSpeechEvent.None);
