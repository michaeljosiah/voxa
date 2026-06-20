using System.Globalization;

namespace Voxa.Audio.Diarization.Onnx;

/// <summary>
/// The pyannote segmentation-3.0 model's hyperparameters, read from the ONNX <c>custom_metadata_map</c> at load
/// time (VLS-005 WS2) — nothing is hard-coded, so a re-exported model carries its own geometry. Mirrors the
/// fields the sherpa-onnx reference reads: window/receptive-field geometry plus the powerset layout.
/// </summary>
internal sealed record PyannoteSegmentationModel(
    int SampleRate,
    int WindowSize,
    int WindowShift,
    int ReceptiveFieldSize,
    int ReceptiveFieldShift,
    int NumSpeakers,
    int PowersetMaxClasses,
    int NumClasses)
{
    /// <summary>Read the geometry from an ONNX session's custom metadata, failing clearly on a non-pyannote model.</summary>
    public static PyannoteSegmentationModel FromMetadata(IReadOnlyDictionary<string, string> meta)
    {
        ArgumentNullException.ThrowIfNull(meta);

        int Get(string key)
        {
            if (!meta.TryGetValue(key, out var raw) || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                throw new InvalidOperationException(
                    $"ONNX model metadata is missing the integer key '{key}' — this does not look like a pyannote " +
                    "segmentation-3.0 export (expected window_size / sample_rate / receptive_field_* / num_speakers / " +
                    "powerset_max_classes / num_classes).");
            return value;
        }

        int windowSize = Get("window_size");
        return new PyannoteSegmentationModel(
            SampleRate: Get("sample_rate"),
            WindowSize: windowSize,
            WindowShift: (int)(0.1 * windowSize), // sherpa reference: 10% window hop
            ReceptiveFieldSize: Get("receptive_field_size"),
            ReceptiveFieldShift: Get("receptive_field_shift"),
            NumSpeakers: Get("num_speakers"),
            PowersetMaxClasses: Get("powerset_max_classes"),
            NumClasses: Get("num_classes"));
    }
}
