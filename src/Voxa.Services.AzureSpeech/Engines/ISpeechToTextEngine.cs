namespace Voxa.Services.AzureSpeech.Engines;

/// <summary>
/// Abstraction over a streaming speech-to-text engine. Lets <see cref="AzureSpeechSttProcessor"/>
/// be unit-tested against an in-memory fake without burning Azure quota.
/// </summary>
public interface ISpeechToTextEngine : IAsyncDisposable
{
    /// <summary>Begin a recognition session. Idempotent guard is implementation-defined.</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>Append PCM audio to the recognition stream.</summary>
    ValueTask WriteAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct);

    /// <summary>Stream of transcription updates (interim + final). Completes when <see cref="StopAsync"/> is called.</summary>
    IAsyncEnumerable<TranscriptionResult> ReadTranscriptsAsync(CancellationToken ct);

    /// <summary>Gracefully end the recognition session and complete the transcript stream.</summary>
    Task StopAsync();
}

/// <summary>One transcription result. <see cref="IsFinal"/> distinguishes interim vs. settled output.</summary>
public sealed record TranscriptionResult(string Text, bool IsFinal, string? Language = null);
