namespace Voxa.Speech;

/// <summary>
/// Vendor-neutral streaming speech-to-text engine. Vendor packages
/// (<c>Voxa.Speech.Azure</c>, <c>Voxa.Speech.OpenAI</c>, etc.) implement this; the generic
/// <see cref="SpeechToTextProcessor"/> consumes it.
/// </summary>
public interface ISpeechToTextEngine : IAsyncDisposable
{
    /// <summary>Begin a recognition session. Implementations should be idempotent.</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>Append PCM audio to the recognition stream.</summary>
    ValueTask WriteAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct);

    /// <summary>Stream of transcription updates (interim + final). Completes on <see cref="StopAsync"/>.</summary>
    IAsyncEnumerable<TranscriptionResult> ReadTranscriptsAsync(CancellationToken ct);

    /// <summary>Gracefully end the recognition session.</summary>
    Task StopAsync();

    /// <summary>
    /// Force any buffered audio to be transcribed immediately. Called by
    /// <see cref="SpeechToTextProcessor"/> when it observes a <see cref="Voxa.Frames.UserStoppedSpeakingFrame"/>
    /// upstream so batch engines (REST Whisper, etc.) can hit the API right at speech-end
    /// instead of waiting for the next periodic timer tick.
    ///
    /// <para>Default: no-op. Streaming engines (Azure SDK, OpenAI Realtime) don't batch and
    /// override this only if they have something to drain.</para>
    /// </summary>
    Task FlushAsync() => Task.CompletedTask;

    /// <summary>
    /// New-utterance signal: <see cref="SpeechToTextProcessor"/> calls this on a
    /// <see cref="Voxa.Frames.UserStartedSpeakingFrame"/>. Streaming engines use it to reset their late-final
    /// anti-bleed window so the new utterance's first final is kept even if it arrives within the window of the
    /// previous flush (e.g. a quick "yes"). Default: no-op (batch engines don't need it).
    /// </summary>
    Task OnUserStartedSpeakingAsync() => Task.CompletedTask;

    /// <summary>
    /// True only if this engine implements eager/speculative STT (VRT-002 WS1) — i.e. it overrides
    /// <see cref="FlushAsync(long)"/> to peek-transcribe (stamp the id, do NOT clear the buffer) and
    /// <see cref="DiscardBufferedAudioAsync"/> to drop a promoted buffer. <see cref="SpeechToTextProcessor"/>
    /// engages the eager path (speculative flush + hold/suppress/promote) only when this is true; otherwise a
    /// <c>SpeculativeUtteranceFrame</c> is ignored by the engine and the turn flushes normally at speech-end.
    ///
    /// <para>Default <c>false</c>. An engine whose <see cref="FlushAsync(long)"/> would do a normal flush MUST
    /// leave this false — otherwise it would emit an <em>untagged</em> final the processor can neither hold nor
    /// drop and would clear its buffer, defeating supersession.</para>
    /// </summary>
    bool SupportsEagerSttFlush => false;

    /// <summary>
    /// Speculative variant of <see cref="FlushAsync()"/> for eager STT (VRT-002 WS1): flush the buffered audio
    /// now and tag the resulting final <see cref="TranscriptionResult"/> with <paramref name="utteranceId"/> so
    /// <see cref="SpeechToTextProcessor"/> can drop it if the VAD later supersedes the utterance. Called only
    /// when <see cref="SupportsEagerSttFlush"/> is true.
    ///
    /// <para>Default is a NO-OP (deliberately not a normal flush): a non-participating engine must never emit an
    /// untagged speculative final or clear its buffer here. Batch engines that opt in (e.g. whisper.cpp) override
    /// this to peek-transcribe-without-clearing + stamp the id. The per-frame <c>CancellationToken</c> does not
    /// reach here — suppression by id, not cancellation, is the guarantee.</para>
    /// </summary>
    Task FlushAsync(long utteranceId) => Task.CompletedTask;

    /// <summary>
    /// Discard buffered audio WITHOUT transcribing it (VRT-002 WS1). Called by
    /// <see cref="SpeechToTextProcessor"/> on the "confirm ⇒ promote" path: a speculative
    /// <see cref="FlushAsync(long)"/> already transcribed the buffered utterance, so when the turn is confirmed
    /// the engine must drop that buffer without producing a second (duplicate) transcription.
    ///
    /// <para>Default: no-op. Streaming engines hold no buffer; batch engines whose speculative flush already
    /// clears don't need it. Batch engines whose <see cref="FlushAsync(long)"/> peeks without clearing (so a
    /// resume can re-transcribe the full utterance — e.g. whisper.cpp) override this to drop the promoted buffer.</para>
    /// </summary>
    Task DiscardBufferedAudioAsync() => Task.CompletedTask;
}

/// <summary>One transcription result from an STT engine.</summary>
/// <param name="Text">The recognised text.</param>
/// <param name="IsFinal">False for an interim hypothesis; true once the utterance settles.</param>
/// <param name="Language">Optional BCP-47 language tag.</param>
/// <param name="UtteranceId">
/// Optional speculative-utterance id (VRT-002 WS1), set by an engine when the result came from a
/// <see cref="ISpeechToTextEngine.FlushAsync(long)"/> speculative flush. Lets <see cref="SpeechToTextProcessor"/>
/// drop a final whose utterance the VAD later superseded. Null for ordinary (non-speculative) results.
/// </param>
public sealed record TranscriptionResult(string Text, bool IsFinal, string? Language = null, long? UtteranceId = null);
