namespace Voxa.Audio.Diarization.Onnx;

/// <summary>
/// Pure (no-ONNX, no-DSP) decoding of pyannote segmentation-3.0 output into absolute-time speech regions
/// (VLS-005 WS2) — a faithful C# port of the sherpa-onnx <c>vad-onnx.py</c> reference, so it is unit-testable
/// on synthetic logits. Steps: powerset class → speech/no-speech per frame; Hamming-weighted overlap-add of the
/// sliding windows onto a global frame timeline; onset/offset (0.5) binarisation; frame index → seconds.
/// </summary>
internal static class PowersetSegmentationDecoder
{
    private const double Onset = 0.5;
    private const double Offset = 0.5;

    /// <summary>
    /// The powerset → per-speaker mapping: row 0 is silence (all zero), then the single-speaker rows, then the
    /// speaker-pair rows — exactly the order pyannote's powerset head emits. Used to mark which classes mean speech.
    /// </summary>
    public static float[][] BuildPowersetMapping(int numClasses, int numSpeakers, int powersetMaxClasses)
    {
        var mapping = new float[numClasses][];
        for (int c = 0; c < numClasses; c++) mapping[c] = new float[numSpeakers];

        int k = 1; // row 0 stays all-zero (silence)
        for (int i = 1; i <= powersetMaxClasses; i++)
        {
            if (i == 1)
            {
                for (int j = 0; j < numSpeakers; j++) { mapping[k][j] = 1; k++; }
            }
            else if (i == 2)
            {
                for (int j = 0; j < numSpeakers; j++)
                    for (int m = j + 1; m < numSpeakers; m++) { mapping[k][j] = 1; mapping[k][m] = 1; k++; }
            }
            else
            {
                throw new NotSupportedException($"powerset_max_classes > 2 is not supported (got {powersetMaxClasses}).");
            }
        }
        return mapping;
    }

    /// <summary>A class index is "speech" when its powerset row activates any speaker (i.e. it isn't the silence row).</summary>
    public static bool[] SpeechClasses(float[][] mapping)
    {
        var speech = new bool[mapping.Length];
        for (int c = 0; c < mapping.Length; c++)
            foreach (var v in mapping[c]) if (v > 0) { speech[c] = true; break; }
        return speech;
    }

    /// <summary>Argmax over a frame's class logits, then map to 1 (speech) / 0 (silence) via <paramref name="speechClasses"/>.</summary>
    public static float FrameActivity(ReadOnlySpan<float> frameLogits, bool[] speechClasses)
    {
        int best = 0;
        float bestVal = frameLogits[0];
        for (int c = 1; c < frameLogits.Length; c++)
            if (frameLogits[c] > bestVal) { bestVal = frameLogits[c]; best = c; }
        return speechClasses[best] ? 1f : 0f;
    }

    /// <summary>
    /// Stitch the per-window binary activity onto one global frame timeline (Hamming-weighted overlap-add),
    /// binarise with onset/offset, and emit absolute-time <see cref="SpeechRegion"/>s.
    /// </summary>
    /// <param name="chunkActivity">One <c>float[framesPerChunk]</c> (0/1) per sliding window, in order.</param>
    /// <param name="m">The model geometry (window/receptive-field/sample-rate) read from the ONNX metadata.</param>
    /// <param name="totalSamples">Length of the original audio (used to trim a zero-padded last chunk).</param>
    /// <param name="hasLastChunk">True when the final window was zero-padded (so its tail isn't real audio).</param>
    public static List<SpeechRegion> FormRegions(
        IReadOnlyList<float[]> chunkActivity, PyannoteSegmentationModel m, int totalSamples, bool hasLastChunk)
    {
        var regions = new List<SpeechRegion>();
        int numChunks = chunkActivity.Count;
        if (numChunks == 0) return regions;

        int framesPerChunk = chunkActivity[0].Length;
        int numFramesGlobal = (int)((m.WindowSize + (numChunks - 1) * (double)m.WindowShift) / m.ReceptiveFieldShift) + 1;
        var classification = new double[numFramesGlobal];
        var count = new double[numFramesGlobal];
        var weight = Hamming(framesPerChunk);

        for (int i = 0; i < numChunks; i++)
        {
            int start = (int)(i * (double)m.WindowShift / m.ReceptiveFieldShift + 0.5);
            var chunk = chunkActivity[i];
            for (int f = 0; f < framesPerChunk; f++)
            {
                int g = start + f;
                if ((uint)g < (uint)numFramesGlobal)
                {
                    classification[g] += chunk[f] * weight[f];
                    count[g] += weight[f];
                }
            }
        }
        for (int g = 0; g < numFramesGlobal; g++) classification[g] /= Math.Max(count[g], 1e-12);

        int usable = numFramesGlobal;
        if (hasLastChunk)
            usable = Math.Clamp((int)(totalSamples / (double)m.ReceptiveFieldShift), 0, numFramesGlobal);
        if (usable == 0) return regions;

        // Frame index → seconds: a half-receptive-field offset centres the frame, as the reference does.
        double scale = m.ReceptiveFieldShift / (double)m.SampleRate;
        double scaleOffset = m.ReceptiveFieldSize / (double)m.SampleRate * 0.5;

        bool active = classification[0] > Onset;
        int regionStart = 0;
        for (int i = 0; i < usable; i++)
        {
            if (active)
            {
                if (classification[i] < Offset)
                {
                    regions.Add(new SpeechRegion(regionStart * scale + scaleOffset, i * scale + scaleOffset));
                    active = false;
                }
            }
            else if (classification[i] > Onset)
            {
                regionStart = i;
                active = true;
            }
        }
        if (active) // close a region still open at the end of the recording
            regions.Add(new SpeechRegion(regionStart * scale + scaleOffset, (usable - 1) * scale + scaleOffset));

        return regions;
    }

    /// <summary>NumPy-compatible Hamming window (<c>0.54 − 0.46·cos(2πn/(M−1))</c>); a single point is weight 1.</summary>
    internal static double[] Hamming(int n)
    {
        var w = new double[n];
        if (n == 1) { w[0] = 1.0; return w; }
        for (int i = 0; i < n; i++) w[i] = 0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (n - 1));
        return w;
    }
}
