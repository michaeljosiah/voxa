# Voxa Roadmap

Items below are explicitly *not* in v0.x but are tracked here so they don't get lost. Ordered roughly by user-impact-per-effort. Pull requests welcome on any of them.

## P0 — Latency: get the cheap chain to ~600 ms end-to-end

The chained `Whisper → gpt-4o-mini → TTS` pipeline currently sits at ~1.6 s from end-of-spoken-words to first bot audio. Realtime API is ~250–400 ms. Here's the breakdown and where to cut:

| Component | Today | Target | How |
|---|---|---|---|
| VAD silence hangover | 800 ms | ~200 ms | **Smart turn detection** (see below) |
| Whisper REST round-trip | 300–500 ms | 100–200 ms | Streaming STT (Deepgram / Azure Streaming) |
| LLM first token | 200–400 ms | unchanged | already at gpt-4o-mini |
| TTS first byte | 300–500 ms | 100–200 ms | Streaming TTS (ElevenLabs / Cartesia) |
| **Total** | **~1.6 s** | **~600 ms** | |

### Smart turn detection (single biggest win)

Pipecat ships `OpenAISmartTurnAnalyzer` and on-device CoreML / `local_smart_turn_v2` / `v3` classifiers in `pipecat/audio/turn/smart_turn/`. They sit *on top of* silence VAD: silence-end fires → classifier says "is the user actually done?" → only then UserStoppedSpeakingFrame fires. With this, `stop_secs=0.2` is safe — within-sentence pauses don't trigger early flush.

Voxa equivalent shape: a new `Voxa.Audio.SmartTurn` package with `ISmartTurnClassifier` interface, plus implementations:
- `OpenAISmartTurnClassifier` — uses a fast LLM (gpt-4o-mini) with a turn-end classification prompt
- `LocalSmartTurnClassifier` — bundled ONNX model (Pipecat's released one, MIT)
- `HttpSmartTurnClassifier` — remote endpoint (matches Pipecat's HTTP smart turn)

Insert between VAD and STT. When the VAD's silence-end fires, classifier evaluates the partial transcription + audio confidence. If "done" → forward `UserStoppedSpeakingFrame`. If "not done" → suppress, keep gate effectively open.

Estimated effort: ~1 week including model integration + tests.

### Streaming STT alternatives

- **Deepgram** — sub-200 ms partial results, very good accuracy, ~$0.43/hr. New `Voxa.Speech.Deepgram` package.
- **Azure Speech Streaming** — already have `Voxa.Speech.Azure` but it uses the SDK's segmented mode. Switch to true streaming for partial results.

### Streaming TTS alternatives

- **Cartesia** — sub-100 ms first-byte, ~$0.025/min. New `Voxa.Speech.Cartesia` package.
- **ElevenLabs streaming** — already in `Voxa.Speech.ElevenLabs`, just need to verify the engine actually streams (some implementations buffer).

## P1 — Echo suppression / double-talk handling

When the user is on speakers (not headphones), the mic picks up the bot's own audio and tries to transcribe it. Browser-side `echoCancellation: true` helps but isn't perfect. Logs show stray "speaking user" / "transcription" events firing during bot playback.

Fix: VAD processor listens for `BotStartedSpeakingFrame` / `BotStoppedSpeakingFrame` (need to make these flow upstream as system frames) and drops all audio frames while the bot is speaking. Optional opt-in `AllowBargeIn=true` for the user-can-interrupt case.

Estimated effort: ~2 days. Requires a small change to frame direction conventions.

## P2 — True barge-in / interruption

Today: user starts talking → SentenceAggregator drops its buffer (good), but the TTS audio already in the WebSocket send queue still plays out (user hears bot finish current sentence). LLM may still be generating. Net effect: bot keeps talking for ~1 sentence after the interrupt.

Pipecat handles this by:
1. Cancelling the LLM run via the active `CancellationTokenSource`
2. Cancelling the TTS engine
3. Sending an `interruption` envelope to the client so it stops audio playback

Voxa partially supports it (SentenceAggregator drops buffer on `UserStartedSpeakingFrame`). The remaining work: thread cancellation through `MicrosoftAgentsProcessor` and have `WebSocketAudioSink` send a `flush` envelope that the JS handles by calling `outCtx.suspend()` or scheduling-zero on pending source nodes.

Estimated effort: ~3 days. Touches multiple processors + the JS playback path.

## P3 — `Aonik.Voice` adapter (originally Phase 4)

Wire Voxa into AONIK as a sibling project to `Aonik.Agents`. Documented in detail in `~/.claude/plans/review-https-github-com-pipecat-ai-pipec-giggly-penguin.md`. Highlights:

- New `src/Aonik.Voice/` project, net10.0
- `WSS /ai/voice` endpoint registered via `app.MapAonikVoiceEndpoints()`
- New `MobileVoicePolicy` auth (NOT `AdminUserPolicy` — that's for admin tools)
- `AonikVoicePipelineFactory` composes the runner per connection
- Resolves agent via existing `IAgentContextualizer.ResolveAsync(...)`
- Creates session via existing `agent.CreateSessionAsync(conversationId, ct)` — passes the persisted `ChatThread.Id` so memory survives reconnects AND flows back to the SSE web chat tab (same ChatThread, two front-ends)
- `voice.system.md` prompt overlay loaded via `IPromptStore` and merged into the orchestrator's system instructions
- Tool calling: stays in MAF — the orchestrator's `ApprovalRequiredAIFunction` instances fire as today; `MicrosoftAgentsProcessor` already streams `ToolCallRequestFrame`s. No `MafToolDispatcherProcessor` needed (deferred from original plan since Voice Live composite is the only path that needs it).

Flutter mobile side: `voxa_voice_client.dart` + streaming PCM player, behind `Aonik:Voice:WebSocketEnabled` feature flag.

Estimated effort: ~2 weeks (server adapter ~1w + mobile client ~1w).

## P4 — Polish

- `LlmResponseStartFrame` / `EndFrame` for explicit turn boundaries (currently inferred from text + speaking events)
- 3-phase function call frames (started / in-progress / done) + WireProtocol envelopes
- `Voxa.Observability` metrics frames + observer (TTFB, token counts, stage latencies) — wires up the Metrics tab placeholder in the demo
- Fix the empty-bot-bubble that occasionally appears when SentenceAggregator emits a near-empty fragment

## Not planned (deferred from original Pipecat scope)

- WebRTC transports (Daily, LiveKit) — WebSocket is enough for mobile-first
- Telephony (Twilio, SIP)
- Vision / image / avatar processors
- Whisker / Tail debugging tools
- mem0, Sentry observers — use OpenTelemetry instead
