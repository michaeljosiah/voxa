using Microsoft.ML.OnnxRuntime;
using Voxa.Audio.Onnx;

namespace Voxa.Audio.Diarization.Onnx;

/// <summary>
/// An <see cref="ISpeakerSegmentation"/> backed by pyannote segmentation-3.0 on the shared Voxa.Audio.Onnx host
/// (VLS-005 WS2). The model takes <b>raw audio</b> (its SincNet front-end is inside the graph — no external
/// STFT/mel), so this engine only frames the audio into the model's sliding windows, runs ORT, and decodes the
/// powerset output into speech regions in pure C# (<see cref="PowersetSegmentationDecoder"/>). Geometry is read
/// from the model's metadata, so a re-export carries its own parameters.
///
/// <para>The weights load once on the process-wide <see cref="OnnxModelHost"/>; this engine does not own/dispose
/// the session. Not thread-safe for concurrent <see cref="Segment"/> on one instance (it reuses no per-call
/// buffers, but treat one engine as single-threaded per the seam's batch usage).</para>
/// </summary>
public sealed class PyannoteOnnxSegmentation : ISpeakerSegmentation
{
    private const int BatchSize = 32; // sherpa reference batches windows in 32s

    private readonly IOnnxSession _session;
    private readonly PyannoteSegmentationModel _model;
    private readonly bool[] _speechClasses;

    /// <param name="modelPath">Resolved path to the pyannote segmentation ONNX (see <see cref="PyannoteSegmentationCatalog"/>).</param>
    /// <param name="host">The shared ONNX session host.</param>
    /// <param name="device">Execution device (CPU default).</param>
    public PyannoteOnnxSegmentation(string modelPath, OnnxModelHost host, OnnxDevice device = OnnxDevice.Cpu)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelPath);
        ArgumentNullException.ThrowIfNull(host);

        _session = host.Load(modelPath, device);
        _model = PyannoteSegmentationModel.FromMetadata(_session.Session.ModelMetadata.CustomMetadataMap);
        var mapping = PowersetSegmentationDecoder.BuildPowersetMapping(_model.NumClasses, _model.NumSpeakers, _model.PowersetMaxClasses);
        _speechClasses = PowersetSegmentationDecoder.SpeechClasses(mapping);
    }

    /// <summary>The sample rate the model runs at (read from its metadata; pyannote-3.0 is 16000).</summary>
    public int SampleRate => _model.SampleRate;

    public IReadOnlyList<SegmentationWindow> Segment(ReadOnlySpan<float> audio, int sampleRate)
    {
        if (sampleRate != _model.SampleRate)
            throw new ArgumentException(
                $"Pyannote segmentation runs at {_model.SampleRate} Hz; got {sampleRate}. Resample upstream.",
                nameof(sampleRate));

        int len = audio.Length;
        double totalSeconds = len / (double)sampleRate;
        if (len == 0) return [new SegmentationWindow(0, 0, Array.Empty<SpeechRegion>())];

        int ws = _model.WindowSize, shift = _model.WindowShift;

        // Sliding windows + a zero-padded last chunk, matching the reference's as_strided + pad. For len > 0 this
        // is always ≥ 1 window (len < ws ⇒ one padded window), so totalChunks can't be 0.
        int numFull = len >= ws ? (len - ws) / shift + 1 : 0;
        bool hasLast = len < ws || (len - ws) % shift != 0;
        int totalChunks = numFull + (hasLast ? 1 : 0);

        var activity = new List<float[]>(totalChunks);
        using var runOptions = new RunOptions();
        string inputName = _session.InputNames[0];
        string outputName = _session.OutputNames[0];

        for (int b = 0; b < totalChunks; b += BatchSize)
        {
            int n = Math.Min(BatchSize, totalChunks - b);
            // Copy each window straight from `audio` into the batch buffer — no full overlap materialisation
            // (windows overlap ~10×, so a global windows[] would be ~10× the audio).
            var batch = new float[n * ws];
            for (int j = 0; j < n; j++)
            {
                int chunk = b + j;
                var dest = batch.AsSpan(j * ws, ws);
                if (chunk < numFull)
                {
                    audio.Slice(chunk * shift, ws).CopyTo(dest);
                }
                else // the zero-padded last chunk; its tail stays zero
                {
                    int off = numFull * shift;
                    int remain = Math.Clamp(len - off, 0, ws);
                    if (remain > 0) audio.Slice(off, remain).CopyTo(dest);
                }
            }

            using var input = OrtValue.CreateTensorValueFromMemory(batch, [n, 1, ws]);
            var inputs = new Dictionary<string, OrtValue> { [inputName] = input };
            using var results = _session.Session.Run(runOptions, inputs, [outputName]);

            var data = results[0].GetTensorDataAsSpan<float>(); // [n, frames, num_classes] flattened
            int classes = _model.NumClasses;
            if (data.Length % (n * classes) != 0)
                throw new InvalidOperationException(
                    $"Pyannote output length {data.Length} is not a multiple of batch {n} × {classes} classes — " +
                    "the model's num_classes metadata disagrees with its output width.");
            int frames = data.Length / (n * classes);
            for (int c = 0; c < n; c++)
            {
                var act = new float[frames];
                for (int f = 0; f < frames; f++)
                    act[f] = PowersetSegmentationDecoder.FrameActivity(data.Slice((c * frames + f) * classes, classes), _speechClasses);
                activity.Add(act);
            }
        }

        var regions = PowersetSegmentationDecoder.FormRegions(activity, _model, len, hasLast);
        return [new SegmentationWindow(0, totalSeconds, regions)];
    }
}
