using System.Text;
using Voxa.Frames;
using Voxa.Processors;

namespace Voxa.Speech;

/// <summary>
/// Buffers <see cref="LlmTextChunkFrame"/>s coming from a streaming LLM and emits whole-sentence
/// <see cref="TextFrame"/>s downstream as soon as a sentence boundary is detected. Lets a downstream
/// <see cref="TextToSpeechProcessor"/> start synthesising the first sentence while the LLM is still
/// generating the rest — Pipecat's <c>SentenceAggregator</c> pattern.
///
/// <para>
/// Sentence boundaries: <c>.</c>, <c>!</c>, <c>?</c>, or newline. Trailing context (e.g. "Mr." or
/// "3.14") is handled by requiring the boundary to be followed by whitespace, end-of-input, or
/// nothing yet — sufficient for natural speech in practice.
/// </para>
///
/// <para>
/// On <see cref="EndFrame"/>, <see cref="UserStartedSpeakingFrame"/> (interruption), or
/// <see cref="InterruptionFrame"/>, any pending buffer is flushed as a final TextFrame so no
/// trailing fragment is silently dropped.
/// </para>
/// </summary>
public sealed class SentenceAggregator : FrameProcessor
{
    private readonly StringBuilder _buffer = new();
    private readonly object _lock = new();
    private int _lastBoundary = -1;     // index in _buffer of the last confirmed sentence boundary
    private bool _firstFlushOfTurn = true;
    private bool _droppingInterruptedTurn; // barge-in mute: drop the cancelled turn's stale chunks until the next turn opens

    /// <summary>Maximum chars to buffer before forcing a flush even without a sentence boundary. Default 500.</summary>
    public int MaxBufferChars { get; init; } = 500;

    /// <summary>
    /// When &gt; 0, the FIRST flush of a turn may also break at a clause boundary (<c>, ; :</c>
    /// followed by whitespace) once at least this many characters are buffered — getting the bot's
    /// first audio out 100–400 ms earlier on long opening sentences. Subsequent flushes use full
    /// sentence boundaries as usual. Default 0 (off). A turn boundary is detected from
    /// <see cref="LlmTurnStartedFrame"/> (or reset by an interruption).
    /// </summary>
    public int EagerFirstChunkMinChars { get; init; }

    public SentenceAggregator() : base("SentenceAggregator") { }

    protected override async ValueTask ProcessFrameAsync(Frame frame, CancellationToken ct)
    {
        switch (frame)
        {
            case LlmTextChunkFrame chunk when !string.IsNullOrEmpty(chunk.Text):
                {
                    string? toFlush = null;
                    lock (_lock)
                    {
                        // Barge-in: chunks from the cancelled turn can still be queued behind the
                        // (system-channel) interruption — dropping the buffer once isn't enough;
                        // stay muted until the next turn opens or the stale answer resumes.
                        if (_droppingInterruptedTurn) return;

                        var text = chunk.Text;
                        int baseLen = _buffer.Length;
                        _buffer.Append(text);

                        // Incremental boundary scan — each new character is examined exactly once,
                        // instead of re-stringifying and rescanning the whole buffer per chunk.
                        // A boundary is . ! ? \n followed by whitespace, or such a char at the very
                        // end of the buffer (end-of-input rule) — same as the old FindLastSentenceBoundary.

                        // Seam: the previous buffer's last char is a boundary char and this chunk
                        // starts with whitespace.
                        if (baseLen > 0 && char.IsWhiteSpace(text[0]) && IsBoundaryChar(_buffer[baseLen - 1]))
                        {
                            _lastBoundary = baseLen - 1;
                        }
                        // Interior of this chunk: boundary char followed by whitespace.
                        for (int i = 0; i < text.Length - 1; i++)
                        {
                            if (IsBoundaryChar(text[i]) && char.IsWhiteSpace(text[i + 1]))
                            {
                                _lastBoundary = baseLen + i;
                            }
                        }
                        // Trailing boundary char at end-of-buffer (end-of-input rule).
                        if (IsBoundaryChar(text[^1]))
                        {
                            _lastBoundary = _buffer.Length - 1;
                        }

                        // Eager first chunk: on the first flush of a turn, a clause boundary
                        // (, ; :) followed by whitespace also counts, once enough is buffered —
                        // gets the opening audio out sooner. Only fills in if no stronger
                        // sentence boundary was already found.
                        if (_lastBoundary < 0 && _firstFlushOfTurn && EagerFirstChunkMinChars > 0 &&
                            _buffer.Length >= EagerFirstChunkMinChars)
                        {
                            if (baseLen > 0 && char.IsWhiteSpace(text[0]) && IsClauseChar(_buffer[baseLen - 1]))
                            {
                                _lastBoundary = baseLen - 1;
                            }
                            for (int i = 0; i < text.Length - 1; i++)
                            {
                                if (IsClauseChar(text[i]) && char.IsWhiteSpace(text[i + 1]))
                                {
                                    _lastBoundary = baseLen + i;
                                }
                            }
                        }

                        if (_lastBoundary >= 0)
                        {
                            toFlush = _buffer.ToString(0, _lastBoundary + 1).Trim();
                            _buffer.Remove(0, _lastBoundary + 1);
                            _lastBoundary = -1;
                            _firstFlushOfTurn = false;
                        }
                        else if (_buffer.Length >= MaxBufferChars)
                        {
                            // Hard cap — emit whatever we have so TTS doesn't stall on a runaway response.
                            toFlush = _buffer.ToString().Trim();
                            _buffer.Clear();
                            _lastBoundary = -1;
                            _firstFlushOfTurn = false;
                        }
                    }

                    if (!string.IsNullOrEmpty(toFlush))
                    {
                        await PushFrameAsync(new TextFrame(toFlush), ct).ConfigureAwait(false);
                    }
                    return;
                }

            case LlmTurnStartedFrame:
                // New turn — the next flush is the turn's first (re-enables eager first-chunk mode)
                // and any barge-in mute lifts: this turn's chunks are fresh, not the stale tail.
                lock (_lock) { _firstFlushOfTurn = true; _droppingInterruptedTurn = false; }
                await PushFrameAsync(frame, ct).ConfigureAwait(false);
                return;

            case UserStartedSpeakingFrame:
            case InterruptionFrame:
                // Drop the buffered partial sentence AND everything else this turn still has queued
                // (see the chunk case) — interrupted, so the response is no longer relevant.
                lock (_lock) { _buffer.Clear(); _lastBoundary = -1; _firstFlushOfTurn = true; _droppingInterruptedTurn = true; }
                await PushFrameAsync(frame, ct).ConfigureAwait(false);
                return;

            case TranscriptionFrame { IsFinal: true } final when !string.IsNullOrWhiteSpace(final.Text):
                // Data-ordered unmute (mirrors TextToSpeechProcessor): the barge-in utterance's final
                // queues behind the stale chunks and ahead of the next turn's — safe reopen point for
                // chains without turn-lifecycle frames.
                lock (_lock) _droppingInterruptedTurn = false;
                await PushFrameAsync(frame, ct).ConfigureAwait(false);
                return;

            default:
                await PushFrameAsync(frame, ct).ConfigureAwait(false);
                return;
        }
    }

    protected override async ValueTask OnEndAsync(EndFrame frame, CancellationToken ct)
    {
        string? leftover;
        lock (_lock)
        {
            leftover = _buffer.Length > 0 ? _buffer.ToString().Trim() : null;
            _buffer.Clear();
            _lastBoundary = -1;
            _firstFlushOfTurn = true;
        }
        if (!string.IsNullOrEmpty(leftover))
        {
            await PushFrameAsync(new TextFrame(leftover), ct).ConfigureAwait(false);
        }
    }

    /// <summary>A char that can end a sentence: <c>. ! ? \n</c>.</summary>
    private static bool IsBoundaryChar(char c) => c is '.' or '!' or '?' or '\n';

    /// <summary>A clause boundary used only for the eager first chunk: <c>, ; :</c>.</summary>
    private static bool IsClauseChar(char c) => c is ',' or ';' or ':';
}
