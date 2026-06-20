namespace Voxa.Audio;

/// <summary>
/// Acoustic echo canceller seam (VRT-003): subtracts the far-end playback signal (the bot's TTS audio)
/// from the near-end mic capture so the user can barge in over speakers without the bot hearing itself.
/// Models speech-core's <c>EchoCancellerInterface</c>. Implementations OWN buffering, frame alignment, and
/// any rate conversion between the far-end and near-end streams — the seam stays deliberately simple.
/// <para>
/// Threading contract: <see cref="FeedReference"/> (TTS/writer thread) may be called CONCURRENTLY with
/// <see cref="CancelEcho"/> (audio thread); both sit on hot paths and must be non-blocking and
/// allocation-light. The implementation owns the thread-safety of its own buffers.
/// </para>
/// The default is <see cref="NullEchoCanceller"/> (passthrough); a real DSP is a separate follow-up item.
/// </summary>
public interface IEchoCanceller
{
    /// <summary>
    /// The PCM sample rate (Hz) this canceller is configured for — the near-end / output rate the chain
    /// runs at. A far-end fed at a different rate is the implementation's problem (it resamples internally).
    /// </summary>
    int SampleRate { get; }

    /// <summary>
    /// Feed a chunk of the far-end reference — the audio being played to the user (bot/TTS PCM). Called from
    /// the TTS/writer thread, concurrently with <see cref="CancelEcho"/>. MUST be non-blocking and
    /// allocation-light; the implementation buffers and time-aligns it against the near-end internally.
    /// <para>
    /// The far-end PCM is at the session's fixed far-end (TTS-output) rate, which the composer supplies to the
    /// canceller's factory at construction (it can differ from <see cref="SampleRate"/>, the near-end rate, in
    /// mixed-rate pipelines) — so the implementation has both rates it needs to resample/align.
    /// </para>
    /// </summary>
    void FeedReference(ReadOnlyMemory<byte> farEndPcm);

    /// <summary>
    /// Remove the far-end echo from one near-end (mic) chunk and return the cleaned PCM. Called once per
    /// inbound <c>AudioRawFrame</c>. May return the input unchanged (passthrough) or a same-length cleaned
    /// buffer; it must not change the sample-count semantics the caller relies on for the frame's duration.
    /// </summary>
    ReadOnlyMemory<byte> CancelEcho(ReadOnlyMemory<byte> nearEndPcm);

    /// <summary>
    /// Reset all adaptive/filter state and drop buffered far-end. Called on session start, on an
    /// interruption epoch, and when the bot stops speaking, so stale echo state never bleeds into a new turn.
    /// </summary>
    void Reset();
}

/// <summary>
/// The default echo canceller: returns the mic audio untouched and ignores the reference feed, so a pipeline
/// built with it is byte-identical to one built before this seam existed. Used whenever a host resolves an
/// <see cref="IEchoCanceller"/> explicitly without a real implementation; the default composition omits the
/// stage entirely (it never even inserts this passthrough) when <c>Voxa:Aec:Engine</c> is unset or "None".
/// </summary>
public sealed class NullEchoCanceller : IEchoCanceller
{
    public static readonly NullEchoCanceller Instance = new();

    public int SampleRate => 0;                                       // rate-agnostic; never inspects audio
    public void FeedReference(ReadOnlyMemory<byte> farEndPcm) { }     // no-op
    public ReadOnlyMemory<byte> CancelEcho(ReadOnlyMemory<byte> nearEndPcm) => nearEndPcm; // passthrough
    public void Reset() { }
}
