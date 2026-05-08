using System.Threading.Channels;
using Voxa.Speech;

namespace Voxa.Speech.Abstractions.Tests;

internal sealed class ScriptedSpeechToTextEngine : ISpeechToTextEngine
{
    private readonly Channel<TranscriptionResult> _transcripts = Channel.CreateUnbounded<TranscriptionResult>();
    private readonly List<byte[]> _writtenAudio = new();
    private readonly object _lock = new();

    public bool Started { get; private set; }
    public bool Stopped { get; private set; }
    public bool Disposed { get; private set; }

    public IReadOnlyList<byte[]> WrittenAudio
    {
        get { lock (_lock) return _writtenAudio.ToList(); }
    }

    public Task StartAsync(CancellationToken ct) { Started = true; return Task.CompletedTask; }

    public ValueTask WriteAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct)
    {
        lock (_lock) _writtenAudio.Add(pcm.ToArray());
        return ValueTask.CompletedTask;
    }

    public IAsyncEnumerable<TranscriptionResult> ReadTranscriptsAsync(CancellationToken ct)
        => _transcripts.Reader.ReadAllAsync(ct);

    public Task StopAsync()
    {
        Stopped = true;
        _transcripts.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public ValueTask QueueTranscriptAsync(string text, bool isFinal, string? language = null)
        => _transcripts.Writer.WriteAsync(new TranscriptionResult(text, isFinal, language));

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _transcripts.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
