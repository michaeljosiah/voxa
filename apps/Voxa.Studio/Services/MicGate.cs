namespace Voxa.Studio.Services;

/// <summary>
/// Half-duplex gate for Talk's mic pump (echo suppression, ROADMAP P1): drops captured frames while the
/// bot is speaking — plus a short hangover for the speaker buffer to drain — so a user on speakers doesn't
/// feed the bot's own output back through VAD → STT → agent in a loop. Opt out with
/// <c>Voxa:Studio:AllowBargeIn=true</c> for full-duplex (barge-in works, but use headphones to avoid echo).
/// Time is injectable so the hangover is deterministically testable.
/// </summary>
internal sealed class MicGate
{
    private readonly bool _allowBargeIn;
    private readonly TimeSpan _hangover;
    private readonly Func<DateTimeOffset> _now;
    private volatile bool _botSpeaking;
    private DateTimeOffset _reopenAt = DateTimeOffset.MinValue;

    public MicGate(bool allowBargeIn, TimeSpan? hangover = null, Func<DateTimeOffset>? now = null)
    {
        _allowBargeIn = allowBargeIn;
        _hangover = hangover ?? TimeSpan.FromMilliseconds(250);
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public void BotStartedSpeaking() => _botSpeaking = true;

    public void BotStoppedSpeaking()
    {
        _botSpeaking = false;
        _reopenAt = _now() + _hangover;
    }

    /// <summary>True when a captured mic frame should be ingested into the pipeline.</summary>
    public bool ShouldIngest() => _allowBargeIn || (!_botSpeaking && _now() >= _reopenAt);
}
