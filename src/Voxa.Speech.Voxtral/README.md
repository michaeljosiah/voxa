# Voxa.Speech.Voxtral

Fully-offline, open-weights **speech-to-text** for the [Voxa](https://github.com/michaeljosiah/Voxa) pipeline,
built on Mistral's **Voxtral-Mini-4B-Realtime** (Apache-2.0) served locally by **vLLM** over its realtime
WebSocket API. Implements the streaming `ISpeechToTextEngine` — pair it with the generic `SpeechToTextProcessor`
from `Voxa.Speech.Abstractions`, or just set `"Voxa:Stt": "Voxtral"` with the meta-package.

This is the **heavy** local tier (VLS-009): unlike whisper.cpp it needs a **GPU (≥ 16 GB)** and a vLLM server.
For a keyless, CPU-friendly local STT, use `Voxa.Speech.WhisperCpp` instead.

## Two honest hosting modes

Voxa does not pin a vLLM binary (the BF16 model is multi-file, ~9 GB, and needs a GPU). You provide the server:

- **Connect-only** — point Voxa at a vLLM realtime server you already run:
  ```jsonc
  "Voxa": {
    "Stt": "Voxtral",
    "Voxtral": { "ServerUrl": "ws://127.0.0.1:8000" }
  }
  ```
- **Managed** — let Voxa launch and own the server process (it drains logs, polls readiness, and kills the
  process tree on shutdown):
  ```jsonc
  "Voxa": {
    "Stt": "Voxtral",
    "Voxtral": {
      "LaunchCommand": "vllm",
      "LaunchArgs": ["serve", "mistralai/Voxtral-Mini-4B-Realtime-2602", "--tokenizer-mode", "mistral"],
      "Host": "127.0.0.1",
      "Port": 8000
    }
  }
  ```

## Configuration

| Key | Default | Notes |
|-----|---------|-------|
| `Voxa:Voxtral:ServerUrl` | – | Connect-only: ws(s) base URL of a running server. Wins over the managed keys. |
| `Voxa:Voxtral:LaunchCommand` / `ExecutablePath` | – | Managed: command on PATH / launcher executable. |
| `Voxa:Voxtral:LaunchArgs` | `[]` | Args for the managed launch target. |
| `Voxa:Voxtral:Host` / `Port` | `127.0.0.1` / `8000` | Managed listen / connect target. |
| `Voxa:Voxtral:Model` | `mistralai/Voxtral-Mini-4B-Realtime-2602` | Sent in the `session.update` handshake. |
| `Voxa:Voxtral:InputSampleRate` | `16000` | Voxtral Realtime requires 16 kHz mono PCM16. |
| `Voxa:Voxtral:DelayMs` | `480` | Realtime delay knob (80–2400; 480 recommended). |
| `Voxa:Voxtral:Language` | `null` | BCP-47 hint; null auto-detects. |
| `Voxa:Voxtral:ReadyTimeoutSeconds` | `180` | How long to wait for a *managed* cold 4B load. |
| `Voxa:Voxtral:MinGpuMemoryGb` | `16` | VRAM floor for the Studio GPU-gated default. |

## Running the model

```bash
# One-time: a GPU box with vLLM (≥ 16 GB VRAM)
vllm serve mistralai/Voxtral-Mini-4B-Realtime-2602 --tokenizer-mode mistral
```

The model is **Apache-2.0** (open weights, redistributable, commercial-OK). vLLM is the only officially-supported
runtime today — there is no ONNX export and the Realtime architecture is not yet upstream in llama.cpp, so a lighter
CPU/GGUF tier is deferred to a follow-up.

## Developing without a GPU

`sidecar/voxtral_realtime_mock.py` is a tiny stand-in that speaks the same `/v1/realtime` envelopes and replays a
canned transcript — so you can wire up and smoke-test the pipeline end-to-end with no GPU or model:

```bash
pip install websockets
python sidecar/voxtral_realtime_mock.py --port 8000
# then: "Voxa:Voxtral:ServerUrl": "ws://127.0.0.1:8000"
```

## Wire protocol

vLLM realtime is OpenAI-Realtime-style JSON over a WebSocket at `/v1/realtime`, and it is **one-shot per
connection**: a session is `session.update` → a "ready" commit → audio → one `commit {final:true}` → streamed
deltas → one `done`, after which the stream is finished. So the engine opens **one connection per utterance**
(at VAD speech-start) and reconnects for the next turn:

```jsonc
// client → server  (per utterance)
{ "type": "session.update", "model": "mistralai/Voxtral-Mini-4B-Realtime-2602", "delay": 480 }  // + "language" when set
{ "type": "input_audio_buffer.commit" }                  // "ready to start"
{ "type": "input_audio_buffer.append", "audio": "<base64 PCM16>" }   // streamed during speech
{ "type": "input_audio_buffer.commit", "final": true }   // VAD speech-end → finalize this utterance

// server → client
{ "type": "transcription.delta", "delta": "the quick brown" }   // → interim
{ "type": "transcription.done",  "text":  "the quick brown fox" } // → final, then this connection is done
{ "type": "error", "error": { "message": "…" } }                 // → faults the transcript stream
```

A bare commit never produces a `done`, and `{"final": true}` ends the stream — so per-utterance reconnection is
required to drive a multi-turn conversation (one persistent socket would either never finalize a turn or stop
after the first). Requires VAD upstream to bracket utterances.

Parsing is total: an unknown or malformed frame is ignored, never throwing out of the receive loop.

License: MIT (this package). The Voxtral model weights are Apache-2.0, provisioned by you via vLLM.
