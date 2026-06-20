# Voxa.Audio.Onnx

The shared **ONNX Runtime session host** for Voxa's local-speech tier (VLS-006). Every ONNX model in
Voxa — VAD, TTS, ASR, enhancement — loads its weights through this host instead of constructing an
`InferenceSession` itself, so a model loads **once per `(path, device)`** and is shared across every
connection on the process.

This package ships the **host + the device seam + the model-descriptor shape** — not a model. Consuming
engines bring their own pinned catalogs (a `ParakeetCatalog`, a `SidonCatalog`, …) the same way Kokoro
and whisper.cpp do.

## What's in the box

- **`OnnxModelHost`** — a process-wide `(path, device)`-keyed session cache. `Load(path, device, hook)`
  returns a shared `IOnnxSession`; concurrent loads of the same key resolve to one instance. Sessions are
  process-lifetime (they hold shared weights); `OnnxModelHost.EvictAll()` disposes and clears them for
  tests and Studio's "unload models."
- **`IOnnxSession`** — a thin, test-fakeable handle exposing the `InferenceSession` (for engines that bind
  `OrtValue`s directly — the zero-allocation steady-state pattern), the input/output names, and the EP that
  actually loaded.
- **`OnnxDevice` + `OnnxDeviceParser`** — the shared `Device` config convention (`cpu` / `auto` / `cuda` /
  `directml` / `coreml`), parsed identically across every ONNX engine.
- **`OnnxModelDescriptor` + `ResolveAsync`** — an ONNX model self-describes as a graph artifact + pinned
  sidecars (tokenizer / vocab / config / extra graphs) and resolves through the unchanged `VoxaModelCache`.

## CPU by default; GPU is opt-in and never bundled

The base package references only the **CPU** `Microsoft.ML.OnnxRuntime` (pinned to the same `1.26.0` as
SileroVad / Kokoro, with `PrivateAssets` so it isn't re-flowed onto consumers). `Device=cpu` is the
default and the only target the bundled runtime supports.

To use a GPU, the **consuming app** adds the matching ORT package itself — Voxa never ships GPU natives,
and the ORT CPU/GPU packages conflict if both land in one process:

```xml
<PackageReference Include="Microsoft.ML.OnnxRuntime.Gpu" Version="1.26.0" /> <!-- in your app, not in Voxa -->
```

With that present, `Device=cuda` (or `directml` / `coreml`) loads the matching EP. Without it, an explicit
GPU device **fails at session creation** with a copy-pasteable remediation, and `Device=auto` falls back to
CPU with a warning — it never hard-fails.

## Not yet here

- The **`OnnxTensors` convenience run-helper** (the `KokoroTtsEngine.RunInference` shape, lifted) lands with
  the first consumer that needs it, once its dtype matrix is known. Today engines use the direct `Session`
  tier.
- VLS-006 ships **no model catalog**; pinned models (and their SHA-256s + cleared licences) live in the
  consuming engine packages (VLS-004 / 005 / 007 / 008, VRT-005).
