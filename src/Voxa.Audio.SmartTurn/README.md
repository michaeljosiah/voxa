# Voxa.Audio.SmartTurn

Smart turn detection for the Voxa pipeline — the single biggest latency lever (ROADMAP P0).

A silence-only VAD can't tell a *within-sentence pause* ("give me the weather for… London") from
*end-of-turn*, so `Voxa:Vad:StopDuration` has to stay conservative (~800 ms) to avoid clipping
thinkers. A smart-turn classifier sits on top of the silence VAD: silence-end fires → the classifier
looks at the recent speech audio → only a **complete** verdict ends the turn. With one wired,
`StopDuration` can drop to ~200 ms without cutting people off.

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

`HttpSmartTurnClassifier` POSTs the recent speech (≈ up to 1 s, 16-bit mono WAV) to `Endpoint` and reads
a completion verdict from the JSON response. The contract is lenient — any of:

- `{ "complete": true }` (or `is_complete` / `completed`)
- `{ "prediction": 1 }` (1 = complete)
- `{ "probability": 0.92 }` (or `completion_probability` / `score`) compared against `Threshold`

Point it at a self-hosted smart-turn model server, or any compatible service. It **fails "complete"**
on error/timeout, so a flaky endpoint never holds the turn open forever.

## Deferred — the on-device ONNX classifier

`LocalSmartTurnClassifier` (a bundled, SHA-256-pinned ONNX model running in-process, no network on the
turn path) is the natural next step and the lowest-latency option. It is **not** in this package yet:
it needs a validated ONNX export (Pipecat's released smart-turn model is MIT) plus an audio-preprocessing
spike to match the model's exact input tensor — the same "needs a model artifact + spike" gate as the
expressive-TTS sidecar. The seam above is ready for it: it just implements `ISmartTurnClassifier`.
