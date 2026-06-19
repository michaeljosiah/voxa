namespace Voxa.Studio.Services;

/// <summary>
/// Half-duplex gate for Talk's mic pump (echo suppression, ROADMAP P1): keeps the mic from ingesting the
/// bot's own audio while it plays on speakers, so a user on speakers doesn't loop the bot's output back
/// through VAD → STT → agent.
///
/// <para>
/// Reopening is driven by how much audio has actually been <b>queued for playback</b> — not by the
/// <c>BotStoppedSpeaking</c> control frame. That frame is priority-routed and arrives while seconds of
/// PCM may still sit in the device's (non-blocking, queue-only) buffer, so reopening on its timestamp
/// would unmute the mic mid-playback and let the feedback loop recur. Instead the gate stays shut until
/// the estimated playback tail drains, plus a short hangover. Opt out with
/// <c>Voxa:Studio:AllowBargeIn=true</c> for full-duplex (use headphones). Time is injectable so the
/// playback model is deterministically testable.
/// </para>
/// </summary>
internal sealed class MicGate
{
    private readonly bool _allowBargeIn;
    private readonly TimeSpan _hangover;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();
    private volatile bool _botSpeaking;
    private DateTimeOffset _playbackEndsAt = DateTimeOffset.MinValue;

    public MicGate(bool allowBargeIn, TimeSpan? hangover = null, Func<DateTimeOffset>? now = null)
    {
        _allowBargeIn = allowBargeIn;
        _hangover = hangover ?? TimeSpan.FromMilliseconds(250);
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>The bot began a turn — close the gate even before the first audio frame is queued.</summary>
    public void BotStartedSpeaking() => _botSpeaking = true;

    /// <summary>
    /// The bot finished generating. This control frame is priority-routed and can arrive long before the
    /// queued audio finishes playing, so it only clears the speaking flag — the playback tail governs when
    /// the mic actually reopens (see <see cref="NoteRenderedAudio"/>).
    /// </summary>
    public void BotStoppedSpeaking() => _botSpeaking = false;

    /// <summary>
    /// Record audio just queued for playback. Models a continuous FIFO queue: a frame starts playing when
    /// the previous tail finishes (or now, if the queue has already drained) and extends the tail by its
    /// own duration.
    /// </summary>
    public void NoteRenderedAudio(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return;
        lock (_gate)
        {
            var now = _now();
            var start = _playbackEndsAt > now ? _playbackEndsAt : now;
            _playbackEndsAt = start + duration;
        }
    }

    /// <summary>Barge-in dropped the queued samples — they won't play, so collapse the tail to now.</summary>
    public void PlaybackFlushed()
    {
        lock (_gate) { _playbackEndsAt = _now(); }
    }

    /// <summary>True when a captured mic frame should be ingested into the pipeline.</summary>
    public bool ShouldIngest()
    {
        if (_allowBargeIn) return true;
        if (_botSpeaking) return false;
        lock (_gate) { return _now() >= _playbackEndsAt + _hangover; }
    }
}
