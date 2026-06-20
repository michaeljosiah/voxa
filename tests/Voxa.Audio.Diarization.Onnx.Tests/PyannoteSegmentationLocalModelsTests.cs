using Voxa.Audio.Diarization;
using Voxa.Audio.Onnx;
using Voxa.Speech;

namespace Voxa.Audio.Diarization.Onnx.Tests;

/// <summary>
/// End-to-end smoke test against the real pinned model (the LocalModels lane provisions it — SHA-256-pinned
/// download on first run, then offline). Validates that the pipeline RUNS — resolve + hash verify, metadata
/// read, sliding-window inference, powerset decode — and emits structurally-valid regions. Detection accuracy
/// isn't asserted (a tune-and-listen concern); the decode logic is covered exactly by
/// <see cref="PowersetSegmentationDecoderTests"/>.
/// </summary>
[Trait("Category", "LocalModels")]
public class PyannoteSegmentationLocalModelsTests
{
    private static VoxaModelCache Cache() =>
        new(new VoxaModelCacheOptions(VoxaModelCacheOptions.ResolveCacheRoot(), Offline: false));

    [Fact]
    public async Task Loads_and_segments_a_clip_end_to_end()
    {
        var path = await Cache().ResolveAsync(PyannoteSegmentationCatalog.Model);

        OnnxModelHost.EvictAll();
        var seg = new PyannoteOnnxSegmentation(path, new OnnxModelHost());
        Assert.Equal(16000, seg.SampleRate); // read from the model's metadata

        // ~12 s: silence, a loud 1 kHz burst in the middle, silence — enough to exercise multiple windows.
        const int sr = 16000;
        var audio = new float[12 * sr];
        for (int i = 4 * sr; i < 8 * sr; i++)
            audio[i] = 0.3f * MathF.Sin(2 * MathF.PI * 1000 * i / sr);

        var windows = seg.Segment(audio, sr);

        var w = Assert.Single(windows);
        Assert.Equal(0.0, w.Start, 3);
        Assert.Equal(12.0, w.End, 1);

        // The engine ran end-to-end and produced ordered, in-bounds, non-overlapping regions.
        double prevEnd = 0;
        foreach (var r in w.Regions)
        {
            Assert.InRange(r.Start, 0.0, 12.5);
            Assert.InRange(r.End, r.Start, 12.5);
            Assert.True(r.Start >= prevEnd - 1e-6, "regions must be ordered and disjoint");
            prevEnd = r.End;
        }

        OnnxModelHost.EvictAll();
    }

    [Fact]
    public async Task Rate_mismatch_fails_fast()
    {
        var path = await Cache().ResolveAsync(PyannoteSegmentationCatalog.Model);

        OnnxModelHost.EvictAll();
        var seg = new PyannoteOnnxSegmentation(path, new OnnxModelHost());
        var audio = new float[8000];
        Assert.Throws<ArgumentException>(() => seg.Segment(audio, 8000)); // the model runs at 16 kHz
        OnnxModelHost.EvictAll();
    }
}
