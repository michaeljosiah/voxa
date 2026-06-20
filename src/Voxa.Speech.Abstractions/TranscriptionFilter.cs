using Voxa.Frames;
using Voxa.Processors;

namespace Voxa.Speech;

/// <summary>
/// Drops <see cref="TranscriptionFrame"/>s that are obvious Whisper / batch-STT hallucinations —
/// the words those models confabulate from breath, room noise, or pure silence. Without this filter,
/// a near-silent audio chunk routinely transcribes to "Thank you.", "Bye.", "you", ".", or
/// "Subscribe to my channel" (Whisper's training set was biased toward YouTube transcripts).
///
/// <para>
/// Three independent checks; a transcript is dropped if ANY rejects it:
/// </para>
/// <list type="bullet">
///   <item>Trimmed text is shorter than <see cref="MinLengthChars"/>.</item>
///   <item>Trimmed lower-cased text matches one of <see cref="ExactBlocklist"/> (case-insensitive).</item>
///   <item>Trimmed lower-cased text contains one of <see cref="SubstringBlocklist"/>.</item>
/// </list>
///
/// <para>
/// All non-final and non-transcription frames pass through unchanged — so the upstream STT can
/// still emit <c>UserStoppedSpeakingFrame</c>s, and the agent processor still sees control frames.
/// </para>
/// </summary>
public sealed class TranscriptionFilter : FrameProcessor
{
    /// <summary>Minimum length (after trim) for a transcript to be kept. Default 2 — drops "." / "you" / "I" etc.</summary>
    public int MinLengthChars { get; init; } = 2;

    /// <summary>Default Whisper hallucinations to drop. Override to customise.</summary>
    public static readonly IReadOnlySet<string> DefaultExactBlocklist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "thank you.", "thank you", "thanks.", "thanks", "thanks for watching.", "thanks for watching",
        "bye.", "bye", "bye-bye.", "bye-bye", "goodbye.", "goodbye",
        "you.", "you", ".", "..", "...", "?", "!",
        "subscribe.", "subscribe", "please subscribe.", "please subscribe",
        "okay.", "okay", "ok.", "ok", "yeah.", "yeah",
        "uh.", "uh", "um.", "um", "hmm.", "hmm", "huh.", "huh",
        "pfft.", "pfft", "psst.", "psst", "sh.", "shh.", "shhh.", "tsk.",
        "[music]", "(music)", "[silence]", "(silence)", "[laughter]", "(laughter)",
        "[applause]", "(applause)", "[breathing]", "(breathing)", "[sigh]", "(sigh)",
    };

    /// <summary>Default substrings (case-insensitive) that trigger a drop, e.g. "subscribe to my channel".</summary>
    public static readonly IReadOnlyList<string> DefaultSubstringBlocklist = new[]
    {
        "subscribe to", "thanks for watching", "thank you for watching",
        "like and subscribe", "see you in the next video",
    };

    /// <summary>Exact-match blocklist (case-insensitive). Defaults to <see cref="DefaultExactBlocklist"/>.</summary>
    public IReadOnlySet<string> ExactBlocklist { get; init; } = DefaultExactBlocklist;

    /// <summary>Substring-match blocklist (case-insensitive). Defaults to <see cref="DefaultSubstringBlocklist"/>.</summary>
    public IReadOnlyList<string> SubstringBlocklist { get; init; } = DefaultSubstringBlocklist;

    /// <summary>How many transcripts have been dropped this session. Useful for diagnostics.</summary>
    public int DropCount { get; private set; }

    public TranscriptionFilter() : base("TranscriptionFilter") { }

    protected override async ValueTask ProcessFrameAsync(Frame frame, CancellationToken ct)
    {
        if (frame is TranscriptionFrame { IsFinal: true } t)
        {
            var trimmed = (t.Text ?? string.Empty).Trim();
            if (ShouldDrop(trimmed))
            {
                DropCount++;
                return; // drop — neither agent nor sink sees it
            }
        }

        await PushFrameAsync(frame, ct).ConfigureAwait(false);
    }

    private bool ShouldDrop(string trimmed)
    {
        if (trimmed.Length < MinLengthChars) return true;

        if (ExactBlocklist.Contains(trimmed)) return true;

        for (int i = 0; i < SubstringBlocklist.Count; i++)
        {
            // OrdinalIgnoreCase already ignores case — no need to allocate a lowercased copy first.
            if (trimmed.Contains(SubstringBlocklist[i], StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}
