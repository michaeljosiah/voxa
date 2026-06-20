# Voxa.Audio.Diarization

Speaker **diarization** for Voxa (VLS-005) — *"who spoke when"*, filling the long-present but always-null
`TranscriptionFrame.SpeakerId` for batch / meeting transcription.

This package ships the **seams + a pure-C# pipeline** — not a model:

- **`ISpeakerSegmentation`** — audio → speech regions (model-backed; e.g. Pyannote).
- **`ISpeakerEmbedding`** — a speech span → a fixed-width speaker vector (model-backed; e.g. WeSpeaker).
- **`IDiarizer`** / **`DiarizationPipeline`** — the orchestrator. The pipeline composes a segmenter + an
  embedder and does the rest in **pure C#**: form regions → embed each → **constrained agglomerative
  clustering by cosine distance** → stable speaker ids, with consecutive same-speaker regions merged.
- **`DiarizerConfig`** — tunables, defaulted to speech-core's values. The one that matters is
  `ClusteringThreshold` (cosine-distance merge ceiling, default `0.715`); `MinSpeakers` / `MaxSpeakers`
  (`0` = auto) force a floor / cap.

## Why the orchestration is dependency-free

The pipeline references **no ML runtime** (and not even `Voxa.Core`) — steps 3–4 take `float[]` embeddings and
emit `DiarizedSegment[]` with no I/O. That is what makes the clustering testable on **hand-built synthetic
embeddings** in the default lane (two tight groups → two speakers, threshold sensitivity, speaker-count caps,
determinism) with no model download, mirroring speech-core's runtime-free `DiarizationPipeline`.

## Not yet here (needs real pinned models / a consumer)

- The **reference ONNX implementations** (Pyannote segmentation + WeSpeaker embedding) live in a separate
  opt-in `Voxa.Audio.Diarization.Onnx` package — deferred until their models are pinned (real SHA-256 +
  cleared licences) and built on the VLS-006 ONNX host.
- The **`voxa transcribe --diarize`** CLI verb that writes `SpeakerId` onto the transcript — the natural first
  consumer, which only does something once the ONNX impls exist.
- Real-time / streaming diarization and speaker **identification** (enrollment against known voices) are
  out of scope (follow-ups once embeddings exist).
