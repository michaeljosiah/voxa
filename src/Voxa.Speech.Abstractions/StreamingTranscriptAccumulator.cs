using System.Threading.Channels;

namespace Voxa.Speech;

/// <summary>
/// Shared interim-display + VAD-gated-final accumulation for streaming STT engines that are NOT on the
/// <see cref="WebSocketSttEngine"/> base (SDK-backed vendors: Google, AWS). Mirrors that base's turn integration
/// exactly: <see cref="OnFragment"/> streams interims (<c>IsFinal:false</c>) for live display and accumulates the
/// vendor's locked segments; <see cref="Flush"/> emits one accumulated <c>IsFinal:true</c> result at the
/// VAD/smart-turn speech-end — so a streaming SDK vendor drives exactly one agent turn per utterance.
/// </summary>
public sealed class StreamingTranscriptAccumulator
{
    private readonly Channel<TranscriptionResult> _channel = Channel.CreateUnbounded<TranscriptionResult>();
    private readonly object _lock = new();
    private readonly List<string> _finalSegments = new();
    private string _interimTail = string.Empty;

    /// <summary>The transcription stream the engine returns from <c>ReadTranscriptsAsync</c>.</summary>
    public IAsyncEnumerable<TranscriptionResult> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);

    /// <summary>Feed one vendor fragment: a locked segment is accumulated, an interim updates the live tail.
    /// Emits the running text as an interim for display (never a Voxa final — that comes from <see cref="Flush"/>).</summary>
    public void OnFragment(string text, bool isSegmentFinal, string? language)
    {
        string running;
        lock (_lock)
        {
            if (isSegmentFinal)
            {
                if (text.Length > 0) _finalSegments.Add(text);
                _interimTail = string.Empty;
            }
            else
            {
                _interimTail = text;
            }
            running = BuildRunning();
        }
        if (!string.IsNullOrEmpty(running))
            _channel.Writer.TryWrite(new TranscriptionResult(running, IsFinal: false, language));
    }

    /// <summary>Emit the accumulated utterance transcript as one final and reset (call at speech-end).</summary>
    public void Flush(string? language)
    {
        string full;
        lock (_lock)
        {
            full = BuildRunning();
            _finalSegments.Clear();
            _interimTail = string.Empty;
        }
        if (!string.IsNullOrWhiteSpace(full))
            _channel.Writer.TryWrite(new TranscriptionResult(full, IsFinal: true, language));
    }

    /// <summary>Complete the transcription stream (call on stop/dispose).</summary>
    public void Complete() => _channel.Writer.TryComplete();

    private string BuildRunning()
    {
        var joined = string.Join(' ', _finalSegments);
        if (_interimTail.Length == 0) return joined;
        return joined.Length == 0 ? _interimTail : joined + " " + _interimTail;
    }
}
