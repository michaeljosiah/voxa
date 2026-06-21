using Voxa.Speech;

namespace Voxa.Speech.Abstractions.Tests;

/// <summary>
/// The shared turn-integration logic used by the SDK streaming engines (Google, AWS): interims update a live
/// running transcript, locked segments accumulate, and one final is emitted on <see cref="StreamingTranscriptAccumulator.Flush"/>.
/// </summary>
public class StreamingTranscriptAccumulatorTests
{
    private static async Task<List<TranscriptionResult>> Drain(StreamingTranscriptAccumulator acc)
    {
        acc.Complete();
        var results = new List<TranscriptionResult>();
        await foreach (var r in acc.ReadAllAsync(CancellationToken.None))
            results.Add(r);
        return results;
    }

    [Fact]
    public async Task Interims_stream_and_flush_emits_one_accumulated_final()
    {
        var acc = new StreamingTranscriptAccumulator();
        acc.OnFragment("hello", isSegmentFinal: false, "en");        // interim
        acc.OnFragment("hello world", isSegmentFinal: true, "en");   // locked segment
        acc.Flush("en");                                             // VAD speech-end

        var results = await Drain(acc);

        Assert.All(results[..^1], r => Assert.False(r.IsFinal)); // everything before the flush is interim
        var final = results[^1];
        Assert.True(final.IsFinal);
        Assert.Equal("hello world", final.Text);
    }

    [Fact]
    public async Task Multiple_locked_segments_join_into_one_final()
    {
        var acc = new StreamingTranscriptAccumulator();
        acc.OnFragment("first part", isSegmentFinal: true, null);
        acc.OnFragment("second part", isSegmentFinal: true, null);
        acc.Flush(null);

        var results = await Drain(acc);

        var final = results[^1];
        Assert.True(final.IsFinal);
        Assert.Equal("first part second part", final.Text);
    }

    [Fact]
    public async Task Flush_with_nothing_buffered_emits_no_final()
    {
        var acc = new StreamingTranscriptAccumulator();
        acc.Flush("en");
        Assert.Empty(await Drain(acc));
    }

    [Fact] // Codex P1: a provider's late segment-final (arriving after the VAD flushed the turn) must not bleed forward.
    public async Task A_late_segment_final_after_flush_does_not_bleed_into_the_next_turn()
    {
        var acc = new StreamingTranscriptAccumulator();
        acc.OnFragment("turn one", isSegmentFinal: true, null);
        acc.Flush(null);                                       // VAD ends turn A
        acc.OnFragment("turn one", isSegmentFinal: true, null);// late provider final for A → must be dropped
        acc.OnFragment("turn two", isSegmentFinal: false, null);// new utterance re-arms accumulation
        acc.OnFragment("turn two", isSegmentFinal: true, null);
        acc.Flush(null);                                       // turn B

        var finals = (await Drain(acc)).Where(r => r.IsFinal).Select(r => r.Text).ToList();
        Assert.Equal(new[] { "turn one", "turn two" }, finals); // not "turn one turn two"
    }

    [Fact] // Codex P1 (round 3): a late interim for the flushed turn must not re-open the window for its late final.
    public async Task A_late_interim_then_late_final_after_flush_does_not_bleed()
    {
        var acc = new StreamingTranscriptAccumulator(); // immediate calls → all within the default window
        acc.OnFragment("turn one", isSegmentFinal: true, null);
        acc.Flush(null);                                          // turn A
        acc.OnFragment("turn one tail", isSegmentFinal: false, null); // late interim for A
        acc.OnFragment("turn one tail", isSegmentFinal: true, null);  // late final for A — must still be dropped
        acc.OnFragment("turn two", isSegmentFinal: false, null);
        acc.OnFragment("turn two", isSegmentFinal: true, null);
        acc.Flush(null);                                          // turn B

        var finals = (await Drain(acc)).Where(r => r.IsFinal).Select(r => r.Text).ToList();
        Assert.Equal(new[] { "turn one", "turn two" }, finals); // A's tail never reaches B
    }

    [Fact] // Codex P2 (round 4): a user-start reset keeps a quick follow-up final that lands inside the window.
    public async Task OnUtteranceStart_keeps_a_quick_followup_final_within_the_window()
    {
        long t = 1000;
        var acc = new StreamingTranscriptAccumulator(() => t, lateFinalWindowMs: 500);
        acc.OnFragment("first", isSegmentFinal: true, null);
        acc.Flush(null);                                    // turn A at t=1000 (window armed)
        t = 1100;                                           // still inside the window
        acc.OnUtteranceStart();                             // user starts speaking again → reset
        acc.OnFragment("yes", isSegmentFinal: true, null);  // quick final-only "yes" — kept because the window was reset
        acc.Flush(null);

        var finals = (await Drain(acc)).Where(r => r.IsFinal).Select(r => r.Text).ToList();
        Assert.Equal(new[] { "first", "yes" }, finals);
    }

    [Fact] // Codex P1 (round 2): a final-only provider (no interims) must not lose every utterance after the first.
    public async Task A_final_only_provider_recovers_after_the_late_final_window()
    {
        long t = 1000;
        var acc = new StreamingTranscriptAccumulator(() => t, lateFinalWindowMs: 500);
        acc.OnFragment("hi", isSegmentFinal: true, null);     // utterance A (final-only)
        acc.Flush(null);                                      // VAD ends A → emits "hi"; window armed at t=1000
        t = 1100;
        acc.OnFragment("hi", isSegmentFinal: true, null);     // A's late tail, within window → dropped
        t = 3000;
        acc.OnFragment("bye", isSegmentFinal: true, null);    // utterance B, after window → accepted (re-armed)
        acc.Flush(null);                                      // emits "bye"

        var finals = (await Drain(acc)).Where(r => r.IsFinal).Select(r => r.Text).ToList();
        Assert.Equal(new[] { "hi", "bye" }, finals); // B not dropped, A's tail not bled
    }
}
