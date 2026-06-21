using System.Threading.Channels;

namespace Voxa.Speech;

/// <summary>
/// Shared interim-display + VAD-gated-final accumulation for streaming STT engines (the WebSocket base and the
/// SDK-backed Google/AWS engines). Mirrors the turn integration once: <see cref="OnFragment"/> streams interims
/// (<c>IsFinal:false</c>) for live display and accumulates the vendor's locked segments; <see cref="Flush"/> emits
/// one accumulated <c>IsFinal:true</c> result at the VAD/smart-turn speech-end — so a streaming vendor drives
/// exactly one agent turn per utterance.
///
/// <para><b>Anti-bleed.</b> When the VAD flushes a turn before the provider has delivered its final, that final
/// arrives just afterwards. Such a late final is dropped (it's the flushed turn's tail, already emitted) so it
/// can't merge into the next turn — but only within a short window after the flush: a non-empty interim, or any
/// final after the window, marks a genuinely new utterance and re-arms accumulation. The window keeps the drop
/// from starving a <em>final-only</em> provider (one that never emits interims) of its later utterances.</para>
/// </summary>
public sealed class StreamingTranscriptAccumulator
{
    private readonly Channel<TranscriptionResult> _channel = Channel.CreateUnbounded<TranscriptionResult>();
    private readonly object _lock = new();
    private readonly List<string> _finalSegments = new();
    private readonly Func<long> _nowMs;
    private readonly long _lateFinalWindowMs;
    private string _interimTail = string.Empty;
    private bool _awaitingNewUtterance; // after a flush: drop the flushed turn's trailing finals until re-armed
    private long _flushedAtMs;

    /// <param name="clock">Monotonic millisecond clock; defaults to <see cref="Environment.TickCount64"/> (injectable for tests).</param>
    /// <param name="lateFinalWindowMs">How long after a flush a segment-final is treated as the flushed turn's late tail (dropped).</param>
    public StreamingTranscriptAccumulator(Func<long>? clock = null, int lateFinalWindowMs = 600)
    {
        _nowMs = clock ?? (static () => Environment.TickCount64);
        _lateFinalWindowMs = lateFinalWindowMs;
    }

    /// <summary>The transcription stream the engine returns from <c>ReadTranscriptsAsync</c>.</summary>
    public IAsyncEnumerable<TranscriptionResult> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);

    /// <summary>Feed one vendor fragment: a locked segment is accumulated, an interim updates the live tail.
    /// Emits the running text as an interim for display (never a Voxa final — that comes from <see cref="Flush"/>).</summary>
    public void OnFragment(string text, bool isSegmentFinal, string? language)
    {
        string running;
        lock (_lock)
        {
            if (_awaitingNewUtterance)
            {
                if (isSegmentFinal)
                {
                    // Late final within the window = the just-flushed turn's tail → drop (anti-bleed). After the
                    // window it's a new utterance from a final-only provider → accept and re-arm.
                    if (_nowMs() - _flushedAtMs < _lateFinalWindowMs) return;
                    _awaitingNewUtterance = false;
                }
                else
                {
                    if (text.Length == 0) return;   // empty interim can't start a new utterance
                    _awaitingNewUtterance = false;  // a real interim marks the new utterance
                }
            }

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
            _awaitingNewUtterance = true; // discard the flushed turn's late provider finals (anti-bleed)
            _flushedAtMs = _nowMs();
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
