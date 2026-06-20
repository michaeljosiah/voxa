using Voxa.Audio.Diarization;

namespace Voxa.Audio.Diarization.Onnx.Tests;

/// <summary>
/// Unit tests for the pure powerset→regions decoder — the validatable core (no model, no network), pinned
/// against the sherpa-onnx reference logic.
/// </summary>
public class PowersetSegmentationDecoderTests
{
    [Fact]
    public void Powerset_mapping_matches_pyannote_layout()
    {
        // seg-3.0: 3 speakers, up to 2 concurrent → 7 classes: silence, 3 singles, 3 pairs (in that order).
        var m = PowersetSegmentationDecoder.BuildPowersetMapping(numClasses: 7, numSpeakers: 3, powersetMaxClasses: 2);

        Assert.Equal([0f, 0f, 0f], m[0]); // silence
        Assert.Equal([1f, 0f, 0f], m[1]);
        Assert.Equal([0f, 1f, 0f], m[2]);
        Assert.Equal([0f, 0f, 1f], m[3]);
        Assert.Equal([1f, 1f, 0f], m[4]); // {0,1}
        Assert.Equal([1f, 0f, 1f], m[5]); // {0,2}
        Assert.Equal([0f, 1f, 1f], m[6]); // {1,2}
    }

    [Fact]
    public void Speech_classes_are_everything_but_silence()
    {
        var mapping = PowersetSegmentationDecoder.BuildPowersetMapping(7, 3, 2);
        Assert.Equal([false, true, true, true, true, true, true], PowersetSegmentationDecoder.SpeechClasses(mapping));
    }

    [Fact]
    public void Frame_activity_is_argmax_then_speech_lookup()
    {
        bool[] speech = [false, true, true]; // class 0 silence, 1/2 speech
        Assert.Equal(0f, PowersetSegmentationDecoder.FrameActivity([0.9f, 0.1f, 0.0f], speech)); // argmax 0 → silence
        Assert.Equal(1f, PowersetSegmentationDecoder.FrameActivity([0.1f, 0.8f, 0.1f], speech)); // argmax 1 → speech
        Assert.Equal(1f, PowersetSegmentationDecoder.FrameActivity([0.1f, 0.2f, 0.7f], speech)); // argmax 2 → speech
    }

    [Fact]
    public void Powerset_max_classes_above_two_is_rejected()
        => Assert.Throws<NotSupportedException>(() => PowersetSegmentationDecoder.BuildPowersetMapping(15, 4, 3));

    private static PyannoteSegmentationModel TestModel(int receptiveFieldSize = 0) => new(
        SampleRate: 16000, WindowSize: 1000, WindowShift: 100,
        ReceptiveFieldSize: receptiveFieldSize, ReceptiveFieldShift: 100,
        NumSpeakers: 3, PowersetMaxClasses: 2, NumClasses: 7);

    [Fact]
    public void Form_regions_single_chunk_two_runs()
    {
        // One full window (no padding); two speech runs → two regions. With a single chunk the Hamming-weighted
        // overlap-add collapses to the raw activity, so the onset/offset boundaries are exact.
        var activity = new[] { new float[] { 0, 1, 1, 0, 0, 1, 1, 0 } };
        var regions = PowersetSegmentationDecoder.FormRegions(activity, TestModel(), totalSamples: 1000, hasLastChunk: false);

        // scale = receptive_field_shift / sample_rate = 100/16000 = 0.00625; offset = 0 here.
        Assert.Equal(2, regions.Count);
        Assert.Equal(0.00625, regions[0].Start, 5);
        Assert.Equal(0.01875, regions[0].End, 5);
        Assert.Equal(0.03125, regions[1].Start, 5);
        Assert.Equal(0.04375, regions[1].End, 5);
    }

    [Fact]
    public void Form_regions_centres_frames_by_half_receptive_field()
    {
        // receptive_field_size 320 → scale_offset = 320/16000*0.5 = 0.01 added to every boundary.
        var activity = new[] { new float[] { 0, 1, 1, 0 } };
        var regions = PowersetSegmentationDecoder.FormRegions(activity, TestModel(receptiveFieldSize: 320), 1000, false);
        Assert.Single(regions);
        Assert.Equal(1 * 0.00625 + 0.01, regions[0].Start, 5);
        Assert.Equal(3 * 0.00625 + 0.01, regions[0].End, 5);
    }

    [Fact]
    public void Form_regions_all_silence_and_all_speech()
    {
        Assert.Empty(PowersetSegmentationDecoder.FormRegions([new float[] { 0, 0, 0, 0 }], TestModel(), 1000, false));

        var all = PowersetSegmentationDecoder.FormRegions([new float[] { 1, 1, 1, 1 }], TestModel(), 1000, false);
        Assert.Single(all);                       // one region spanning the speech, closed at the recording end
        Assert.Equal(0.0, all[0].Start, 5);
    }

    [Fact]
    public void Form_regions_no_chunks_is_empty()
        => Assert.Empty(PowersetSegmentationDecoder.FormRegions([], TestModel(), 0, false));

    [Fact]
    public void Hamming_window_matches_numpy()
    {
        Assert.Equal([1.0], PowersetSegmentationDecoder.Hamming(1));
        var w = PowersetSegmentationDecoder.Hamming(8);
        Assert.Equal(0.08, w[0], 5);   // endpoints
        Assert.Equal(0.08, w[7], 5);
        Assert.Equal(w[0], w[7], 9);   // symmetric
        Assert.Equal(w[1], w[6], 9);
        Assert.True(w[3] > 0.9 && w[4] > 0.9); // peak near the centre
    }
}
