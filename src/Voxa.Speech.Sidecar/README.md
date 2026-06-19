# Voxa.Speech.Sidecar (VVL-002)

Expressive, multilingual, voice-**cloning** local TTS for the Voxa pipeline — the models that don't
have a clean .NET/ONNX path (XTTS-v2, OpenVoice, …) — run in a **separate process** and stream PCM
back over a tiny stdio protocol. This is the same out-of-process isolation `Voxa.Speech.Piper` uses
for espeak-ng, so the project stays predominantly .NET: the sidecar is an **opt-in heavy tier**, not
a Python rewrite.

```
SidecarTtsEngine (.NET, ITextToSpeechEngine)
        │  request JSON line  → stdin
        ▼
   voxa_tts_sidecar  (frozen Python binary / dev script)
        │  {"sample_rate":N}\n  then  [uint32 LE len][PCM16]…  [len 0]  → stdout
        ▼
SidecarTtsEngine yields the PCM frames into the pipeline
```

## Status (what's shipped here vs. deferred)

**Shipped (this package):** the .NET integration — `SidecarTtsEngine` (`ITextToSpeechEngine`), the
`SidecarProtocol` wire format (unit-tested over an in-memory stream), the `Sidecar` provider
descriptor, and the runnable Python sidecar source under `sidecar/`. The engine works **today** with a
sidecar you point it at.

**Deferred (needs a Python build environment + an audio-quality spike — the VVL-002 gate):**
- **The frozen, SHA-256-pinned per-platform binaries** and their `VoxaModelCache` catalog + auto-download.
  No binary is fabricated or pinned here — there is nothing to hash yet. Build one (below) and set
  `Voxa:Sidecar:ExecutablePath`, or run the script in dev mode.
- **The model spike: XTTS-v2 vs OpenVoice** (quality / latency / licence) to choose the default engine.
- **Local voice cloning** (`IVoiceCloneProvider` via `ResolveCloner`) — it rides the *same* transport as a
  future `"mode": "clone"` request; the seam already exists in `Voxa.Speech.Abstractions` (VVL-001).

> ⚠️ Heavy tier: a real frozen binary bundles PyTorch and is multi-GB and accelerator-specific. On CPU
> these models miss the live first-audio budget — their natural home is a Studio generation/voiceover
> surface (VST), not the low-latency live pipeline, unless on GPU.

## Use it

Opt in from your host (it is **not** registered by the `Voxa` meta-package):

```csharp
services.AddVoxa(configuration, voxa => voxa.AddProvider(SidecarDescriptors.Tts));
```

Dev mode — run the bundled script with your Python (after `pip install TTS`):

```json
{
  "Voxa": {
    "Tts": "Sidecar",
    "Sidecar": {
      "PythonScript": "sidecar/voxa_tts_sidecar.py",
      "PythonExe": "python",
      "Voice": "default",
      "Language": "en"
    }
  }
}
```

Production — point at a frozen binary:

```json
{ "Voxa": { "Tts": "Sidecar", "Sidecar": { "ExecutablePath": "/opt/voxa/voxa-tts-sidecar" } } }
```

`Voice` may be a voice id or a path to a reference clip for zero-shot cloning (engine-dependent).
With Coqui TTS missing, the sidecar falls back to a sine-tone so the protocol stays exercisable.

## Build a frozen binary (PyInstaller)

```bash
pip install TTS pyinstaller            # XTTS-v2 via Coqui TTS
pyinstaller --onefile --name voxa-tts-sidecar sidecar/voxa_tts_sidecar.py
# → dist/voxa-tts-sidecar  →  set Voxa:Sidecar:ExecutablePath to it
```

To finish VVL-002, build this per platform/accelerator, host the artifacts, record each SHA-256, and
add a pinned `VoxaModelArtifact` catalog so the cache can verify-and-download them like every other
Voxa model.

## Wire protocol

- **Request** (one JSON line on the sidecar's stdin): `{"text","voice","language","sample_rate","mode"}`.
- **Response** on stdout: one JSON header line `{"sample_rate":N}` (or `{"error":"…"}`), then
  length-prefixed PCM16 frames `[uint32 little-endian length][bytes]`, ended by a zero-length frame.
- stdout is the binary channel; the sidecar logs to **stderr** only (and forces binary stdout on Windows).
