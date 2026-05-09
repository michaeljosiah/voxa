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
}

/// <summary>One transcription result from an STT engine.</summary>
/// <param name="Text">The recognised text.</param>
/// <param name="IsFinal">False for an interim hypothesis; true once the utterance settles.</param>
/// <param name="Language">Optional BCP-47 language tag.</param>
public sealed record TranscriptionResult(string Text, bool IsFinal, string? Language = null);
