using Voxa.Speech;

namespace Voxa.Audio.Diarization.Onnx;

/// <summary>
/// The pinned pyannote segmentation-3.0 artifact (VLS-005 WS2). The weights are the **MIT-licensed** model
/// (© CNRS); this pins the **sherpa-onnx export** of it (ungated, plain <c>.onnx</c> — so it resolves through
/// <see cref="VoxaModelCache"/> with no archive step and no HuggingFace gate). The SHA-256 was sourced from the
/// real artifact (the HF mirror is byte-identical to the sherpa GitHub release), never fabricated.
/// </summary>
public static class PyannoteSegmentationCatalog
{
    /// <summary>The pyannote segmentation-3.0 ONNX graph (raw-audio in, powerset speaker-activity out).</summary>
    public static VoxaModelArtifact Model { get; } = new(
        Id: "pyannote/segmentation-3.0/model.onnx",
        DownloadUrl: new Uri("https://huggingface.co/csukuangfj/sherpa-onnx-pyannote-segmentation-3-0/resolve/main/model.onnx"),
        Sha256: "220ad67ca923bef2fa91f2390c786097bf305bceb5e261d4af67b38e938e1079",
        SizeBytes: 5_992_913);
}
