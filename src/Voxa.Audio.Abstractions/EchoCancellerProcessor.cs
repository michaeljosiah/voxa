using Voxa.Frames;
using Voxa.Processors;

namespace Voxa.Audio;

/// <summary>
/// Runs an <see cref="IEchoCanceller"/> over the near-end mic stream. Placed by the composer as the first
/// stage, before the VAD: on each <see cref="AudioRawFrame"/> it returns a frame carrying the cleaned PCM
/// (or, for <see cref="NullEchoCanceller"/>, the same buffer). Every other frame is forwarded unchanged.
/// Resets the canceller on session start and on an interruption epoch so adaptive state never carries stale
/// echo into a new turn. Pair with <see cref="EchoReferenceTapProcessor"/>, which feeds the far-end reference.
/// </summary>
public sealed class EchoCancellerProcessor : FrameProcessor
{
    private readonly IEchoCanceller _aec;

    public EchoCancellerProcessor(IEchoCanceller aec) : base("EchoCanceller")
        => _aec = aec ?? throw new ArgumentNullException(nameof(aec));

    protected override ValueTask OnStartAsync(StartFrame frame, CancellationToken ct)
    {
        _aec.Reset(); // StartFrame itself is forwarded by ProcessFrameAsync below
        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnInterruptionAsync(InterruptionFrame frame, CancellationToken ct)
    {
        _aec.Reset(); // barge-in epoch: drop stale far-end + filter state
        return ValueTask.CompletedTask;
    }

    protected override async ValueTask ProcessFrameAsync(Frame frame, CancellationToken ct)
    {
        if (frame is AudioRawFrame audio)
            await PushFrameAsync(audio with { Pcm = _aec.CancelEcho(audio.Pcm) }, ct).ConfigureAwait(false);
        else
            await PushFrameAsync(frame, ct).ConfigureAwait(false); // forward Start/End/system/everything else
    }
}

/// <summary>
/// The far-end reference tap. Placed by the composer after the TTS stage, on the outbound bot-audio path: it
/// feeds each bot <see cref="AudioRawFrame"/> into the session's shared <see cref="IEchoCanceller"/> via
/// <see cref="IEchoCanceller.FeedReference"/> as it is produced (speech-core's "auto far-end feed"), then
/// forwards the frame unchanged — it only observes, so the sink is never modified and never stalled by it.
/// <para>
/// It also <see cref="IEchoCanceller.Reset"/>s the shared canceller on the downstream turn-edge markers — a
/// barge-in (<see cref="UserStartedSpeakingFrame"/>, the granular pipeline's barge-in signal, which emits no
/// <see cref="InterruptionFrame"/>), an explicit interruption, or the bot finishing
/// (<see cref="BotStoppedSpeakingFrame"/>). The near-end <see cref="EchoCancellerProcessor"/> sits upstream of
/// the VAD and never sees those markers, so without this the canceller would apply stale bot far-end to the
/// user's next mic frames.
/// </para>
/// </summary>
public sealed class EchoReferenceTapProcessor : FrameProcessor
{
    private readonly IEchoCanceller _aec;

    public EchoReferenceTapProcessor(IEchoCanceller aec) : base("EchoReferenceTap")
        => _aec = aec ?? throw new ArgumentNullException(nameof(aec));

    protected override async ValueTask ProcessFrameAsync(Frame frame, CancellationToken ct)
    {
        switch (frame)
        {
            case AudioRawFrame audio:
                _aec.FeedReference(audio.Pcm); // observe the bot's outbound audio as the far-end reference
                break;
            case UserStartedSpeakingFrame:  // barge-in marker the upstream near-end stage can't see
            case InterruptionFrame:         // explicit interruption epoch
            case BotStoppedSpeakingFrame:   // bot turn ended — the far-end stream is done
                _aec.Reset();               // drop stale far-end so it isn't subtracted from the next turn's mic
                break;
        }
        await PushFrameAsync(frame, ct).ConfigureAwait(false); // forward unchanged
    }
}
