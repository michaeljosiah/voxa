# Voxa.Speech.Kokoro

Local/offline **high-quality** text-to-speech for the Voxa pipeline using
[Kokoro-82M](https://huggingface.co/hexgrad/Kokoro-82M) (Apache-2.0) on ONNX Runtime, in-process.

**No API key. No network after the first-run model download.** The quality tier of Voxa's local
speech stack — speech that rivals commercial cloud voices, on your own CPU (see
`Voxa.Speech.Piper` for the faster, lighter speed tier).

## Usage

With the `Voxa` meta-package the descriptor is pre-registered — configuration is all you need:

```json
{
  "Voxa": {
    "Tts": "Kokoro",
    "Kokoro": { "Voice": "af_heart", "Precision": "fp16" }
  }
}
```

À la carte:

```csharp
services.AddVoxa(configuration, voxa => voxa.AddProvider(KokoroDescriptors.Tts));
```

## How it works

Synthesis runs **in-process** on ONNX Runtime (already in Voxa's dependency tree via SileroVad) —
model weights load once per process and are shared across connections. Only phonemization leaves
the process: a stateless per-sentence `espeak-ng` CLI call (~10–30 ms), resolved from `PATH` or a
pinned per-platform download. Output is fixed at 24 kHz — the same rate as the cloud TTS default,
so swapping a cloud voice for Kokoro doesn't change the wire session at all.

**Licensing note:** espeak-ng is GPL-3.0 and stays behind a process boundary. This package links
no GPL code and must never reference KokoroSharp (it bundles espeak-ng natives into consumer
output). Enforced by a dependency-graph gate test, not by review.

## Configuration (`Voxa:Kokoro`)

| Key | Default | Notes |
|---|---|---|
| `Voice` | `af_heart` | Catalog: `af_heart`, `af_bella`, `am_michael`, `bf_emma`, `bm_george` |
| `Precision` | `fp16` | `fp32` (~330 MB), `fp16` (~163 MB), `int8` (~92 MB, fastest, CI default) |
| `ModelPath` / `VoicePath` | – | Explicit paths; bypass catalog + cache |
| `EspeakPath` | – | Explicit espeak-ng binary; else `PATH`, else pinned download |
| `EspeakVoice` | inferred | `a*` voices → `en-us`, `b*` → `en-gb` |
| `Speed` | `1.0` | >1 faster speech; range (0, 3] |
| `MaxConcurrentSyntheses` | `2` | Process-wide cap on parallel ONNX runs |

`OutputSampleRate` overrides are **rejected at startup** — Kokoro-82M is 24 kHz only.
