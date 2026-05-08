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
    /// </summary>
    IAsyncEnumerable<byte[]> SynthesizeAsync(string text, CancellationToken ct);
}
