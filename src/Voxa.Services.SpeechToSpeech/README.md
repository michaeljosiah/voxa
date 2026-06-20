# Voxa.Services.SpeechToSpeech

A **full-duplex speech-to-speech composite** for the Voxa pipeline (VRT-005) — the local-model peer of the cloud
realtime composites (`OpenAIRealtimeProcessor` / `AzureVoiceLiveProcessor`).

`SpeechToSpeechProcessor` slots in exactly where those do — one processor that is the whole voice loop
(model-owned VAD/turn-taking, full-duplex, native interruption) — but it is driven by an in-process (or sidecar)
**`ISpeechToSpeechSession`** instead of a wire transport. It emits the **same frame vocabulary** the cloud
composites do, so Studio's Talk view, the diagnostics hub, and any sink can't tell a third-party realtime API
from a local model:

| Direction | Frames |
|---|---|
| In | `AudioRawFrame` (user audio → the session), `InterruptionFrame` (barge-in → `session.CancelAsync`) |
| Out | `AudioRawFrame` (agent audio), `LlmTextChunkFrame`, `BotStartedSpeakingFrame`/`BotStoppedSpeakingFrame`, `UserStartedSpeakingFrame`/`UserStoppedSpeakingFrame`, `InterruptionFrame`, upstream `ErrorFrame` |

## The seam

`ISpeechToSpeechSession` (in `Voxa.Speech.Abstractions`) models speech-core's `FullDuplexSpeechInterface`:
`AppendUserAudioAsync`, `RespondAsync` (a stream of `SpeechToSpeechChunk` carrying agent audio + text + the
model's own speaking-edge events), `SetVoiceAsync`, `SetSystemPromptAsync`, `ResetSessionAsync`, `CancelAsync`.
It is shaped deliberately parallel to what the cloud realtime processors do internally, so the composite is a
true third member of that family rather than a parallel universe.

## What ships vs. what's deferred

- **Ships:** the seam + the composite processor, with frame parity tested against a **fake session** (no model).
- **Deferred (VRT-005 WS3 — spike-gated):** a concrete `ISpeechToSpeechSession`. A real local speech-to-speech
  model (PersonaPlex / Moshi-class) is GB-scale and often GPU-only, so it lands behind a quality/feasibility
  spike, likely on a GPU or out-of-process host (reusing the VLS-002 / sidecar patterns). A **cloud S2S provider
  could validate the seam first**. Function-calling (`ToolCallRequestFrame`/`ToolCallResultFrame`) is a documented
  optional extension once a model that supports it lands.
- **Deferred (small follow-up):** config-driven selection (a `Voxa:Mode = SpeechToSpeech` key, or a registered
  provider name, so `UseDefaults()` short-circuits to the composite). Today the composite is reached by
  constructing it directly / `MapVoxaVoice(...).UseProcessor(...)`, exactly as a custom processor is.
