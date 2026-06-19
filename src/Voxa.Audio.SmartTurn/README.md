# Voxa.Audio.SmartTurn

Smart turn detection for the Voxa pipeline — the single biggest latency lever (ROADMAP P0).

A silence-only VAD can't tell a *within-sentence pause* ("give me the weather for… London") from
*end-of-turn*, so `Voxa:Vad:StopDurationMs` has to stay conservative (~800 ms) to avoid clipping
thinkers. A smart-turn classifier sits on top of the silence VAD: silence-end fires → the classifier
looks at the recent speech audio → only a **complete** verdict ends the turn. With one wired,
that timeout can drop to ~200 ms without cutting people off.

## Do I need Python?

**No — not for Voxa, and not to opt out of smart turn.** Voxa.Core, the full pipeline, the cloud
providers, and the *local* speech tier (whisper.cpp, Piper, Kokoro, Silero — all native libraries, no
interpreter) run on pure .NET. Smart turn is opt-in: skip `AddVoxaSmartTurn(...)` and you keep the
classic silence VAD with zero new dependencies.

Python enters only if you choose the **`Sidecar`** provider below — it runs the *real* turn model
(`pipecat-ai/smart-turn-v3`) out-of-process so the Whisper feature extraction happens natively in
Python instead of a fragile C# port. The **`Http`** provider needs no local Python (point it at any
model server), and the in-process ONNX path (no Python at all) is the [deferred next step](#deferred--the-in-process-onnx-classifier).

## How it plugs in

The integration seam ships in the framework (no new processor needed):

- `ISmartTurnClassifier` (in `Voxa.Speech.Abstractions`) — `IsTurnCompleteAsync(recentSpeechPcm, sampleRate, ct)`.
- The VAD's `VoxaVadSettings.ConfirmTurnEnd` / `SileroVadOptions.ConfirmTurnEnd` callback.
- `DefaultVoicePipelineComposer` resolves any registered `ISmartTurnClassifier` and wires it into the
  VAD automatically — **zero-cost when none is registered** (classic silence-only behavior, unchanged).

So a host opts in by registering a classifier; everything downstream is automatic.

## Use it (HTTP classifier)

Opt in after `AddVoxa(...)` — it is **not** registered by the meta-package (it adds a call on the
turn-taking path):

```csharp
services.AddVoxa(configuration);
services.AddVoxaSmartTurn(configuration);   // reads Voxa:SmartTurn
```

```json
{
  "Voxa": {
    "Vad": { "Engine": "Silero", "StopDurationMs": "250" },
    "SmartTurn": {
      "Provider": "Http",
      "Endpoint": "http://localhost:8000/predict",
      "ApiKey": "",
      "Threshold": "0.5",
      "TimeoutMs": "300"
    }
  }
}
```

`HttpSmartTurnClassifier` POSTs the recent speech (the current turn, up to ~8 s, 16-bit mono WAV) to `Endpoint` and reads
a completion verdict from the JSON response. The contract is lenient — any of:

- `{ "complete": true }` (or `is_complete` / `completed`)
- `{ "prediction": 1 }` (1 = complete)
- `{ "probability": 0.92 }` (or `completion_probability` / `score`) compared against `Threshold`

Point it at a self-hosted smart-turn model server, or any compatible service. It **fails "complete"**
on error/timeout, so a flaky endpoint never holds the turn open forever.

## Use it (Python sidecar — the real model, local)

Runs `pipecat-ai/smart-turn-v3` in a Voxa-managed Python process, so the model's Whisper feature
extraction happens natively (no C# port to validate). Best when you have Python locally and want the
real model without standing up a separate HTTP server.

```json
{
  "Voxa": {
    "Vad": { "Engine": "Silero", "StopDurationMs": "250" },
    "SmartTurn": {
      "Provider": "Sidecar",
      "PythonExe": "python",
      "PythonScript": "sidecar/voxa_smart_turn_sidecar.py",
      "Model": "pipecat-ai/smart-turn-v3",
      "Threshold": "0.5"
    }
  }
}
```

The script ships in this package under `sidecar/` (copied next to your build output). One-time setup:

```bash
pip install onnxruntime transformers huggingface_hub numpy
```

Voxa launches the process lazily on the first turn, serializes requests over a tiny stdio protocol
(JSON header + 16-bit mono PCM → `{"probability": x}`), drains its stderr to the log, and relaunches it
if it dies. It waits for the model to load (bounded by `SidecarReadyTimeoutMs`, default 60 s — the first
run downloads the model) before the first prediction, then bounds each prediction by `SidecarTimeoutMs`
(default 2 s). Like the HTTP path it **fails "complete"** on any error or timeout — and the script itself
degrades to always-complete (logging why) if the model or its deps are missing, so a bad Python
environment never strands a turn. Set `ExecutablePath` instead of `PythonExe`/`PythonScript` to point at a
frozen (PyInstaller) binary with no interpreter on the box.

## Deferred — the in-process ONNX classifier

`LocalSmartTurnClassifier` (a bundled, SHA-256-pinned ONNX model running in-process, **no network and no
Python** on the turn path) is the lowest-latency option and the only fully Python-free *local* path. It
is **not** in this package yet: it needs the audio-preprocessing spike to reproduce the model's exact
Whisper-mel input tensor in C# — the gate that motivated the Python sidecar above (correct preprocessing
for free). The seam is ready for it: it just implements `ISmartTurnClassifier`, same as the others.
