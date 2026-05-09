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

    /// <summary>Maximum chars to buffer before forcing a flush even without a sentence boundary. Default 500.</summary>
    public int MaxBufferChars { get; init; } = 500;

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
                        _buffer.Append(chunk.Text);

                        // Find the LAST sentence boundary in the buffer; flush everything up to and
                        // including it. Anything after stays buffered (might be the start of the next sentence).
                        var content = _buffer.ToString();
                        var lastBoundary = FindLastSentenceBoundary(content);

                        if (lastBoundary >= 0)
                        {
                            toFlush = content[..(lastBoundary + 1)].Trim();
                            _buffer.Clear();
                            _buffer.Append(content[(lastBoundary + 1)..]);
                        }
                        else if (_buffer.Length >= MaxBufferChars)
                        {
                            // Hard cap — emit whatever we have so TTS doesn't stall on a runaway response.
                            toFlush = _buffer.ToString().Trim();
                            _buffer.Clear();
                        }
                    }

                    if (!string.IsNullOrEmpty(toFlush))
                    {
                        await PushFrameAsync(new TextFrame(toFlush), ct).ConfigureAwait(false);
                    }
                    return;
                }

            case UserStartedSpeakingFrame:
            case InterruptionFrame:
                // Drop the buffered partial sentence — interrupted, so the response is no longer relevant.
                lock (_lock) _buffer.Clear();
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
        }
        if (!string.IsNullOrEmpty(leftover))
        {
            await PushFrameAsync(new TextFrame(leftover), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Returns the index of the last char that ends a sentence. A boundary is one of <c>. ! ? \n</c>
    /// followed by whitespace or end-of-input — protects "Mr." / "3.14" from being split.
    /// </summary>
    private static int FindLastSentenceBoundary(string s)
    {
        for (int i = s.Length - 1; i >= 0; i--)
        {
            var c = s[i];
            if (c == '.' || c == '!' || c == '?' || c == '\n')
            {
                bool followedByWhitespaceOrEnd = i == s.Length - 1 || char.IsWhiteSpace(s[i + 1]);
                if (followedByWhitespaceOrEnd) return i;
            }
        }
        return -1;
    }
}
