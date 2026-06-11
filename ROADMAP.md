# Voxa Roadmap

Items below are explicitly *not* in v0.x but are tracked here so they don't get lost. Ordered roughly by user-impact-per-effort. Pull requests welcome on any of them.

## P0 — Latency: get the cheap chain to ~600 ms end-to-end

> **VPS-001 update (perf optimization spec, `docs/specifications/voxa-performance-optimization-spec.html`).**
> Several P0 levers have shipped: **streaming Azure TTS** (`StartSpeakingTextAsync` + `AudioDataStream`,
> TTFB ~150 ms vs 300–500 ms buffered), **connection warmup** on HTTP speech engines (first-turn TLS
> handshake moved to session start), a configurable **`StopDuration`** hangover, an **eager first
> sentence** flush (`SentenceAggregator.EagerFirstChunkMinChars`), and a **`voxa.turn.ttfb`** metric so
> the number is finally measurable. The **smart-turn classifier** is still outstanding, but its
> integration seam now exists: `SileroVadOptions.ConfirmTurnEnd`. See `docs/performance-tuning.md`.

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

~~Insert between VAD and STT~~ **Integration point already shipped (VPS-001):** `SileroVadOptions.ConfirmTurnEnd`. The VAD invokes the classifier at its silence timeout with the last ~1 s of speech audio; return `true` → emit `UserStoppedSpeakingFrame`, `false` → treat as a mid-sentence pause and keep the gate open (re-evaluated after another `StopDuration` of silence). The classifiers above plug into this delegate — no new processor insertion or frame suppression needed. With a classifier wired, `StopDuration` can safely drop to ~200 ms.

Estimated effort: ~3 days (was ~1 week — the pipeline integration half is done; remaining work is the classifier implementations + model bundling + tests).

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

> **VPS-001 update.** The server half shipped: `WebSocketAudioSink` now drains sends through an
> epoch-stamped outbound queue and **purges queued bot audio from before an interruption** (the
> `interruption` envelope jumps ahead of the stale audio). So the bot's already-queued audio no
> longer plays out after a barge-in. Remaining: cancel the in-flight LLM/TTS run through
> `MicrosoftAgentsProcessor`, and confirm the JS client flushes its local playback buffer on the
> `{"type":"interruption"}` envelope.

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
- `Voxa.Observability` metrics frames + observer (TTFB, token counts, stage latencies) — wires up the Metrics tab placeholder in the demo. *(Partially shipped by VPS-001: `VoxaMetrics` meter with `voxa.turn.ttfb` + `voxa.sink.queue_depth`; per-stage latencies tracked in P7 below.)*
- Fix the empty-bot-bubble that occasionally appears when SentenceAggregator emits a near-empty fragment

## P5 — Developer experience: five lines to a voice bot

The biggest adoption lever. Target: a working voice endpoint in ~5 lines plus one config block, with no knowledge of frames required.

- **`AddVoxa()` + config-bound providers** — `services.AddVoxa(builder.Configuration)` reads a `"Voxa"` config section (`"Stt": "OpenAI"`, `"Tts": "ElevenLabs"`, `"Profile": "LowLatency"`) and registers engines/options in DI, so `app.MapVoxaVoice("/voice").UseDefaults()` composes the whole chained pipeline. Named **profiles** ("LowLatency" / "Quality" / "Cheap") bundle the tuning knobs from `docs/performance-tuning.md` (VAD hangover, eager first chunk, channel capacities) so users never have to learn them individually. `IHttpClientFactory` integration feeds/replaces `VoxaHttp.Shared` for hosts that need custom handlers or proxies.
- **`Voxa` meta-package + `dotnet new voxa-server` template** — one NuGet package pulling Core + Transports.WebSocket + AspNetCore with sensible defaults, and a project template that scaffolds the sample-server shape (voice endpoint, JS client page, appsettings placeholders).
- **Official JS client — `@voxa/client` on npm** — typed wire protocol generated from the `WireMessages` DTOs (so client and server can't drift), mic capture via AudioWorklet, streaming PCM playback, and playback-buffer **flush on the `interruption` envelope** — the missing client half of barge-in (P2); today every consumer reimplements this by hand.

Estimated effort: ~1.5 weeks (AddVoxa ~3d, meta-package + template ~2d, JS client ~4d).

## P6 — Capability

- ✅ **Local/offline speech tier** — shipped as VLS-001 ([spec](docs/specifications/voxa-local-speech-spec.html), [guide](docs/local-speech.md)): `Voxa.Speech.WhisperCpp` (STT), `Voxa.Speech.Piper` (fast TTS), and `Voxa.Speech.Kokoro` (quality TTS) with a shared SHA-256-pinned model cache (first-run download / offline mode), a keyless `Echo` agent, startup model warm-up, and a zero-network CI conversation lane. Develop without API keys, deploy air-gapped, run zero-cost CI conversations.
- **Session resilience / reconnect** — a dropped mobile WebSocket shouldn't lose the conversation. The hello envelope gains an optional resume token; the host maps it to conversation state (AONIK already has `ChatThread.Id` for exactly this). On resume the sink replays the last unfinished bot turn.
- **Typed frontend tools** — source-generate the JSON schema from a C# delegate so tool calling becomes `voice.UseTool("show_chart", (string city, int days) => ...)` instead of hand-written `ArgumentsJson` plumbing; include an approval-required wrapper matching MAF's `ApprovalRequiredAIFunction`.
- **Multi-agent handoff** — switch the active `IAgentTurnDriver` (persona / department) mid-call on a control frame, preserving transport and VAD state.

## P7 — Operability & configurability

- **Per-stage latency waterfall** — frames already carry `PtsMicros`; a lightweight `StageLatencyProcessor` + per-turn breakdown (VAD close → STT final → LLM first token → TTS first byte → first audio on the wire) recorded as `voxa.stage.latency` histograms makes `voxa.turn.ttfb` *diagnosable*, not just observable. A small `/voxa/debug` page in the sample (live waterfall per turn) doubles as the demo's "wow" view.
- **Runtime control envelope** — client-sent `{"type":"configure", ...}` to adjust VAD thresholds / voice / language mid-session (mobile acoustic environments vary wildly), guarded by a host-side allowlist.
- **Conversation test harness** — extend `Voxa.Testing` with a scripted-conversation runner: WAV (or text) in → ordered transcript/frame expectations + latency budgets out, deterministic clock, runnable in CI. Also the cure for timing-flaky tests (e.g. the MicrosoftAgents shutdown test under parallel suite load).
- **Wire protocol doc + versioning** — `docs/wire-protocol.md` generated from the `WireMessages` DTOs, plus a `"v": 1` field in the hello envelope so future protocol changes can be negotiated instead of breaking.

## Not planned (deferred from original Pipecat scope)

- WebRTC transports (Daily, LiveKit) — WebSocket is enough for mobile-first
- Telephony (Twilio, SIP)
- Vision / image / avatar processors
- Whisker / Tail debugging tools
- mem0, Sentry observers — use OpenTelemetry instead
