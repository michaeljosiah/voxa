# Voxa.Audio.Diarization.Onnx

The reference **ONNX implementations** for Voxa speaker diarization (VLS-005 WS2) — the model-backed engines
that plug into the seams shipped by `Voxa.Audio.Diarization` and run on the shared `Voxa.Audio.Onnx` host.

## `PyannoteOnnxSegmentation` (`ISpeakerSegmentation`)

Speaker segmentation / speech-region detection backed by **pyannote segmentation-3.0** (MIT, © CNRS).

The reason it's a clean ONNX-on-host fit: the model's **SincNet front-end is inside the graph**, so it takes
**raw 16 kHz audio** — there's no external STFT/mel to implement or get numerically wrong. This engine only:

1. frames the audio into the model's sliding windows (geometry read from the ONNX **metadata** — nothing
   hard-coded, so a re-export carries its own parameters),
2. runs the graph on the `OnnxModelHost` (weights load once, process-wide), and
3. decodes the **powerset** output into absolute-time speech regions in **pure C#**
   (`PowersetSegmentationDecoder` — a faithful port of the sherpa-onnx reference: powerset→speech per frame,
   Hamming-weighted overlap-add of the windows, onset/offset binarisation). That decoding is unit-tested on
   synthetic logits — no model needed.

```csharp
var host = new OnnxModelHost();
var path = await cache.ResolveAsync(PyannoteSegmentationCatalog.Model, ct); // pinned, SHA-256-verified
var segmentation = new PyannoteOnnxSegmentation(path, host);
var pipeline = new DiarizationPipeline(segmentation, embedding); // embedding = WeSpeaker (follow-up)
```

## Pinned model

`PyannoteSegmentationCatalog.Model` pins the **sherpa-onnx export** of pyannote-3.0 — an **ungated, plain
`.onnx`** (the official `pyannote/segmentation-3.0` repo is HF-gated; this byte-identical mirror isn't), so it
resolves through `VoxaModelCache` with no archive step or HF token. SHA-256 sourced from the real artifact.

## Status / follow-ups

- **Shipped:** the segmentation engine + the pinned model + the pure decoder (unit-tested) + a `LocalModels`
  smoke test that runs the real model end-to-end.
- **Follow-ups:** the `ISpeakerEmbedding` ONNX impl (**WeSpeaker**, CC-BY-4.0 — needs an Fbank front-end) to
  complete the full diarization stack, and the `voxa transcribe --diarize` CLI consumer.
