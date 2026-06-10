namespace Voxa.Speech;

/// <summary>
/// Vendor-neutral streaming text-to-speech engine. Vendor packages implement this; the generic
/// <see cref="TextToSpeechProcessor"/> consumes it.
/// </summary>
public interface ITextToSpeechEngine : IAsyncDisposable
{
    /// <summary>Initialize the engine. Called once before any synthesis.</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>
    /// Synthesise <paramref name="text"/>, yielding raw PCM chunks as they're produced. Yielding
    /// chunked output lets the consumer pipe to a transport without buffering the whole utterance.
    ///
    /// <para>
    /// Each yielded <see cref="ReadOnlyMemory{T}"/> is only valid until the next
    /// <c>MoveNextAsync</c> — engines may hand back a pooled/reused buffer. A consumer that needs to
    /// retain a chunk past the next iteration must copy it. <see cref="TextToSpeechProcessor"/>
    /// copies each chunk into the <c>AudioRawFrame</c> it emits.
    /// </para>
    /// </summary>
    IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(string text, CancellationToken ct);
}
