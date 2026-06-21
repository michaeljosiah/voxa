using System.Threading.Channels;

namespace Voxa.Speech;

/// <summary>
/// Shared interim-display + VAD-gated-final accumulation for streaming STT engines (the WebSocket base and the
/// SDK-backed Google/AWS engines). Mirrors the turn integration once: <see cref="OnFragment"/> streams interims
/// (<c>IsFinal:false</c>) for live display and accumulates the vendor's locked segments; <see cref="Flush"/> emits
/// one accumulated <c>IsFinal:true</c> result at the VAD/smart-turn speech-end — so a streaming vendor drives
/// exactly one agent turn per utterance.
///
/// <para><b>Anti-bleed.</b> When the VAD flushes a turn before the provider has finalized it, the provider keeps
/// emitting that utterance's tail (interims and/or finals) just afterwards. A segment-final arriving within a
/// short window after the flush is therefore dropped as the flushed turn's tail (it was already emitted), so it
/// can't merge into the next turn. The gate is purely time-based: interims are always applied for display but
/// never re-open the window, and a final after the window is kept (so a <em>final-only</em> provider — one that
/// never emits interims — isn't starved of its later utterances).</para>
/// </summary>
public sealed class StreamingTranscriptAccumulator
{
    private readonly Channel<TranscriptionResult> _channel = Channel.CreateUnbounded<TranscriptionResult>();
    private readonly object _lock = new();
    private readonly List<string> _finalSegments = new();
    private readonly Func<long> _nowMs;
    private readonly long _lateFinalWindowMs;
    private string _interimTail = string.Empty;
    private bool _flushed;
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
        lock (_lock)
        {
            // Drop a segment-final that lands within the late-final window after a flush — it's the just-flushed
            // turn's tail. Interims never re-open this window, so a late interim followed by a late final can't
            // sneak the previous turn into the next one.
            if (isSegmentFinal && _flushed && _nowMs() - _flushedAtMs < _lateFinalWindowMs)
                return;

            if (isSegmentFinal)
            {
                if (text.Length > 0) _finalSegments.Add(text);
                _interimTail = string.Empty;
            }
            else
            {
                _interimTail = text;
            }

            // Write under the lock (the channel is unbounded, so TryWrite never blocks) so a concurrent Flush's
            // final on the data-loop thread can't be reordered ahead of this interim from the receive-loop thread.
            var running = BuildRunning();
            if (!string.IsNullOrEmpty(running))
                _channel.Writer.TryWrite(new TranscriptionResult(running, IsFinal: false, language));
        }
    }

    /// <summary>Emit the accumulated utterance transcript as one final and reset (call at speech-end).</summary>
    public void Flush(string? language)
    {
        lock (_lock)
        {
            var full = BuildRunning();
            _finalSegments.Clear();
            _interimTail = string.Empty;
            // Only arm the anti-bleed window when this flush actually emitted a final. An empty flush — the VAD
            // ended a turn before the provider produced any text — has nothing to bleed from, so the turn's real
            // (if late) final must be kept rather than dropped as a tail.
            _flushed = !string.IsNullOrWhiteSpace(full);
            if (_flushed)
            {
                _flushedAtMs = _nowMs();
                _channel.Writer.TryWrite(new TranscriptionResult(full, IsFinal: true, language)); // under lock: ordering
            }
        }
    }

    /// <summary>
    /// Reset the anti-bleed window because a new utterance has begun (driven by <c>UserStartedSpeakingFrame</c>).
    /// The previous flush's late finals have stopped arriving by now, so the new utterance's finals — even a quick
    /// one that lands inside the time window — are kept rather than dropped.
    /// </summary>
    public void OnUtteranceStart()
    {
        lock (_lock) _flushed = false;
    }

    /// <summary>Complete the transcription stream (call on stop/dispose). Flush any buffered text first.</summary>
    public void Complete() => _channel.Writer.TryComplete();

    private string BuildRunning()
    {
        var joined = string.Join(' ', _finalSegments);
        if (_interimTail.Length == 0) return joined;
        return joined.Length == 0 ? _interimTail : joined + " " + _interimTail;
    }
}
