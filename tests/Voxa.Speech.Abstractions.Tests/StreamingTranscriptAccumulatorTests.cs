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
}
