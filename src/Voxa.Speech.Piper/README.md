# Voxa.Speech.Piper

Local/offline text-to-speech for the Voxa pipeline using [Piper](https://github.com/rhasspy/piper).

**No API key. No network after the first-run voice/binary download.** Fast on CPU (RTF ≈ 0.05) —
the speed tier of Voxa's local speech stack (see `Voxa.Speech.Kokoro` for the quality tier).

## Usage

With the `Voxa` meta-package the descriptor is pre-registered — configuration is all you need:

```json
{
  "Voxa": {
    "Tts": "Piper",
    "Piper": { "Voice": "en_US-lessac-medium" }
  }
}
```

À la carte:

```csharp
services.AddVoxa(configuration, voxa => voxa.AddProvider(PiperDescriptors.Tts));
```

## How it works

The official `piper` executable runs as a **warm child process** per voice (pooled, default 2),
synthesizing one sentence per request — which is exactly the unit Voxa's `SentenceAggregator`
delivers. First use downloads the pinned per-platform piper build (~20 MB) and the voice
(~25–115 MB), SHA-256-verified.

**Licensing note:** piper's phonemizer (espeak-ng) is GPL-3.0. It stays isolated behind the piper
process boundary; this package links no GPL code and must never gain an in-process espeak-ng
dependency.

## Configuration (`Voxa:Piper`)

| Key | Default | Notes |
|---|---|---|
| `Voice` | `en_US-lessac-medium` | Catalog: `en_US-lessac-medium/high`, `en_US-amy-low`, `en_GB-alan-medium`, `de_DE-thorsten-medium`, `fr_FR-siwis-medium`, `es_ES-davefx-medium` |
| `VoicePath` | – | Explicit `.onnx` (json sibling required); **requires** `OutputSampleRate` |
| `ExecutablePath` | – | Explicit piper binary; else `PATH`, else pinned download |
| `OutputSampleRate` | inferred | From the voice-name quality suffix: `-low`/`-x_low` → 16000, else 22050 |
| `LengthScale` | `1.0` | <1 faster speech, >1 slower; range (0, 4] |
| `MaxProcesses` | `2` | Warm piper hosts per voice |

The engine re-verifies the announced rate against the voice's own config at startup and fails
loudly on mismatch — wrong-speed audio is a startup error, not a runtime mystery.
