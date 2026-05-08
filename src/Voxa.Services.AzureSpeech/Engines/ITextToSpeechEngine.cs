namespace Voxa.Services.AzureSpeech.Engines;

/// <summary>
/// Abstraction over a streaming text-to-speech engine. Lets <see cref="AzureSpeechTtsProcessor"/>
/// be unit-tested against an in-memory fake.
/// </summary>
public interface ITextToSpeechEngine : IAsyncDisposable
{
    /// <summary>Initialize the engine. Called once before any synthesis.</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>
    /// Synthesize <paramref name="text"/> and yield raw PCM chunks as they're produced.
    /// Yielding chunked output lets consumers pipe to a transport without buffering the whole utterance.
    /// </summary>
    IAsyncEnumerable<byte[]> SynthesizeAsync(string text, CancellationToken ct);
}
