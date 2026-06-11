# Voxa.Speech.WhisperCpp

Local/offline speech-to-text for the Voxa pipeline using [whisper.cpp](https://github.com/ggml-org/whisper.cpp)
via the MIT-licensed [Whisper.net](https://github.com/sandrohanea/whisper.net) bindings.

**No API key. No network after the first-run model download.** Develop without cloud accounts,
run zero-cost CI conversations, deploy air-gapped.

## Usage

With the `Voxa` meta-package the descriptor is pre-registered — configuration is all you need:

```json
{
  "Voxa": {
    "Stt": "WhisperCpp",
    "WhisperCpp": { "Model": "base.en" }
  }
}
```

À la carte:

```csharp
services.AddVoxa(configuration, voxa => voxa.AddProvider(WhisperCppDescriptors.Stt));
```

## How it behaves

Whisper is an utterance transcriber, not a streaming recognizer. The Voxa VAD gates audio, and the
engine transcribes once per utterance when speech ends — so transcripts are **final-only** (no
interim hypotheses), arriving roughly `RTF × utterance length` after the user stops talking.
For interactive development use `tiny.en` or `base.en` with the `LowLatency` profile.

Model weights load **once per process** and are shared across connections.

## Configuration (`Voxa:WhisperCpp`)

| Key | Default | Notes |
|---|---|---|
| `Model` | `base.en` | `tiny`, `tiny.en`, `base`, `base.en`, `small`, `small.en` + `-q5_1` quantized variants |
| `ModelPath` | – | Explicit GGML path; bypasses catalog + cache |
| `Language` | `en` | `auto`/empty = language detection (slower) |
| `Threads` | min(4, cores) | whisper.cpp inference threads |
| `Translate` | `false` | Translate-to-English mode |

`InputSampleRate` overrides are **rejected at startup** — whisper models are 16 kHz mono only.

Models are downloaded on first run (SHA-256-verified, cached under `%LOCALAPPDATA%\voxa\models` /
`~/.cache/voxa/models`, override with `VOXA_MODEL_CACHE` or `Voxa:Models:CachePath`). Air-gapped
hosts set `Voxa:Models:Offline = true` and pre-provision the cache — a miss is a startup error
whose message contains the exact provisioning instructions.
