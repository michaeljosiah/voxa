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

> **M2 update (VRT-002 + VRT-003 shipped).** Eager/speculative STT now starts transcription **before** the
> end-of-turn hangover elapses (`SileroVadOptions.EagerSttDelay`, on in LowLatency/Cheap) — with utterance-id
> suppression so a resumed (or smart-turn-rejected) speculative pass is dropped before it becomes a turn. Three
> turn-taking robustness knobs landed (empty-STT recovery, `MaxUtteranceDuration` force-split,
> `MaxResponseDuration` cap), all off in `Default`. And the **`IEchoCanceller` seam** (VRT-003,
> `Voxa.Audio.Abstractions`) gives true barge-in over speakers a first-class plug — see P1 below. The two
> interruption knobs (`MinInterruptionDuration` debounce, `InterruptionRecoveryTimeout`) are deferred to a
> dedicated follow-up — both need bot-speaking-aware barge-in plus the sink's epoch interplay. See
> `docs/speech-core-parity-program.md`.

The chained `Whisper → gpt-4o-mini → TTS` pipeline currently sits at ~1.6 s from end-of-spoken-words to first bot audio. Realtime API is ~250–400 ms. Here's the breakdown and where to cut:

| Component | Today | Target | How |
|---|---|---|---|
| VAD silence hangover | 800 ms | ~200 ms | **Smart turn detection** (see below) |
| Whisper REST round-trip | 300–500 ms | 100–200 ms | Streaming STT (Deepgram / Azure Streaming) |
| LLM first token | 200–400 ms | unchanged | already at gpt-4o-mini |
| TTS first byte | 300–500 ms | 100–200 ms | Streaming TTS (ElevenLabs / Cartesia) |
| **Total** | **~1.6 s** | **~600 ms** | |

### Smart turn detection (single biggest win)

> **VPS-002 update (shipped).** The framework integration is complete end-to-end: `ISmartTurnClassifier`
> (in `Voxa.Speech.Abstractions`), `VoxaVadSettings.ConfirmTurnEnd` plumbed through `SileroVadDescriptors`,
> and `DefaultVoicePipelineComposer` auto-wiring any DI-registered classifier into the VAD (zero-cost when
> absent). A new opt-in **`Voxa.Audio.SmartTurn`** package ships `HttpSmartTurnClassifier` + `AddVoxaSmartTurn`
> (`Voxa:SmartTurn`). With one registered, `Voxa:Vad:StopDurationMs` can safely drop to ~200 ms. **Remaining:**
> the on-device `LocalSmartTurnClassifier` (a SHA-256-pinned ONNX model + an audio-preprocessing spike to
> match the model's input tensor) — the seam is ready for it.
> **Now specced:** [VLS-010](docs/specifications/vls-010-local-smart-turn-spec.html) — pinned BSD-2-Clause
> Smart Turn v3 artifact (hash verified against the live distribution) on the VLS-006 ONNX host, with the
> Whisper-style log-mel front-end landing as shared DSP for VLS-007 reuse.

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

> **VRT-003 update (shipped — the seam).** Voxa now ships the `IEchoCanceller` seam in
> `Voxa.Audio.Abstractions`: an `EchoCancellerProcessor` placed **before** the VAD plus a far-end tap (after TTS)
> that feeds the bot's own audio as the reference, wired by the composer from `Voxa:Aec:Engine` (default `None`
> ⇒ byte-identical). This is the cleaner fix than gating mic audio during bot playback — a real DSP (WebRTC APM /
> SpeexDSP) drops in as an opt-in `Voxa.Audio.Aec.*` follow-up package. AEC removes the bot's audio so the
> VAD/STT see only the user; pair it with VRT-002's (deferred) interruption debounce for full robustness.

Original sketch (superseded by VRT-003): VAD processor listens for `BotStartedSpeakingFrame` / `BotStoppedSpeakingFrame` (need to make these flow upstream as system frames) and drops all audio frames while the bot is speaking. Optional opt-in `AllowBargeIn=true` for the user-can-interrupt case.

Estimated effort: ~2 days. Requires a small change to frame direction conventions.

## P2 — True barge-in / interruption

> **VPS-001 update.** The server half shipped: `WebSocketAudioSink` now drains sends through an
> epoch-stamped outbound queue and **purges queued bot audio from before an interruption** (the
> `interruption` envelope jumps ahead of the stale audio). So the bot's already-queued audio no
> longer plays out after a barge-in. Remaining: cancel the in-flight LLM/TTS run through
> `MicrosoftAgentsProcessor`, and confirm the JS client flushes its local playback buffer on the
> `{"type":"interruption"}` envelope.
>
> **VRT-002 WS2 update (shipped, PR #100).** The cancel half landed: `AgentLoopProcessor` cancels the
> in-flight turn on user speech and emits a real `InterruptionFrame`; the aggregator and TTS mute their
> stale tails. Barge-in now *works* — and is a hair trigger (any detected speech interrupts, including
> backchannels). **Next:** [VRT-006](docs/specifications/vrt-006-turn-taking-strategies-spec.html) —
> interruption gating (`IInterruptionPolicy`, min-words + backchannel lexicon), engine-signaled
> end-of-turn as a composable `TurnEnd` source, and formalized input-mute policies.

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

- ✅ **`AddVoxa()` + config-bound providers (shipped)** — `services.AddVoxa(builder.Configuration)` reads a `"Voxa"` config section (`"Stt": "OpenAI"`, `"Tts": "ElevenLabs"`, `"Profile": "LowLatency"`) and registers engines/options in DI, so `app.MapVoxaVoice("/voice").UseDefaults()` composes the whole chained pipeline. Named **profiles** ("LowLatency" / "Quality" / "Cheap") bundle the tuning knobs from `docs/performance-tuning.md` (VAD hangover, eager first chunk, channel capacities) so users never have to learn them individually. `IHttpClientFactory` integration feeds/replaces `VoxaHttp.Shared` for hosts that need custom handlers or proxies.
- **`Voxa` meta-package + `dotnet new voxa-server` template** — one NuGet package pulling Core + Transports.WebSocket + AspNetCore with sensible defaults, and a project template that scaffolds the sample-server shape (voice endpoint, JS client page, appsettings placeholders).
- **Official JS client — `@voxa/client` on npm** — speced as **VDX-005** ([spec](docs/specifications/vdx-005-js-client-spec.html)); scoped breakdown below. **Do this first** of the remaining P5 items.
- **Custom conversation memory under `UseDefaults()` — `IVoiceAgentConfigurator`** — speced as **VDX-006** ([spec](docs/specifications/vdx-006-custom-conversation-memory-spec.html)): a one-interface host seam so an app with its own durable conversation store keeps the composer (registered VAD + latency profile + diagnostics taps) instead of abandoning it to swap the agent stage. Resolved per connection; **byte-identical when unused**. Surfaced by the Ada voice-agent review.
- ✅ **Background agent delegation (talker/thinker split) — shipped** — speced as **VDX-008** ([spec](docs/specifications/vdx-008-background-agent-spec.html)), implemented WS1–WS3 + Studio surfacing: a fast interaction model keeps the voice-latency budget while a heavyweight second `IAgentTurnDriver` runs tools/browsing off the critical path; explicit `delegate_task` opt-in (no mirroring), frontend-tool-style frame round-trip, results re-enter as relevance-gated turns with data-ordered hold/release. Composer keyed-driver seam (`AddVoxaBackgroundAgent`); **byte-identical when unused**. Guide: [docs/background-agent.md](docs/background-agent.md); sample: `samples/Voxa.Samples.BackgroundAgentServer`.

### `@voxa/client` (npm) — the official browser/JS client *(start here — [VDX-005 spec](docs/specifications/vdx-005-js-client-spec.html))*

> **Why first.** The server is already a 5-line library call (`samples/Voxa.Samples.MinimalServer/Program.cs`); the *client* is the part every consumer re-hand-rolls — AudioWorklet mic capture, gap-free PCM playback, and the barge-in flush. It is also the missing half of true barge-in (P2): `WebSocketAudioSink` already purges queued bot audio on interruption (epoch bump), but only the client can stop audio it has already handed to the Web Audio graph. And it is *upstream* of the `dotnet new voxa-server` template and any future `voxa-server` tool — both produce an endpoint that needs a real client, not the throwaway test page. A complete reference implementation already exists end-to-end in `samples/Voxa.Samples.MinimalServer/wwwroot/{index.html,recorder-worklet.js}`; this item productizes it and closes the two gaps that page leaves open (tool-call round-trip, protocol-version checking).

**Surface area (v0.1).** One `VoxaClient` over the *existing* wire protocol — the happy path needs no protocol changes:

```ts
const client = new VoxaClient("wss://host/voice", { hello?: {...} });
await client.connect();                 // resolves on the `session` envelope
client.onTranscription(t => …)          // { text, isFinal, language?, speakerId? }
client.onText(chunk => …)               // streamed assistant text ({type:"text"})
client.onSpeaking(s => …)               // { who:"bot"|"user", started }
client.onToolCall(async c => {          // { callId, name, argumentsJson }
  client.sendToolResult(c.callId, resultJson, isError?);
});
client.onStatus / onError / onEnd
client.sendText("…"); client.end(); client.disconnect();
```

It owns: mic `getUserMedia` (echoCancellation/noiseSuppression/AGC on), an `AudioContext` at the server-announced `inputSampleRate`, the recorder worklet (20 ms Int16 frames), gap-free scheduled playback at `outputSampleRate`, and **`flushPlayback()` on both `interruption` *and* `speaking{who:"user",started:true}`** (stop scheduled source nodes, reset the play cursor) — the existing page already proves this is the correct trigger pair.

**Wire protocol = a versioned contract, generated so it can't drift.** The downstream side is *already* versioned: `SessionInfoFrame(… ProtocolVersion = 1)` emits `{"type":"session","v":1,…}`. Formalize it:
1. **Make inbound envelopes real DTOs.** Outbound envelopes are clean records in `src/Voxa.Transports.WebSocket/Protocol/WireMessages.cs`, but the inbound side (`end`/`text`/`toolResult`) is hand-parsed in `WireProtocol.TryParseClientMessage`. Add inbound record structs so *both* directions share one source of truth.
2. **Generate TS from the DTOs.** A small generator (reflect over the `[JsonSerializable]` envelope records) emits a single `voxa-wire.schema.json`; `json-schema-to-typescript` produces `protocol.ts`. A CI **golden check** (same instinct as the existing byte-identical wire tests) fails if the committed TS is stale — client and server cannot drift.
3. **Client honors `session.v`.** Expose it and warn (or refuse — configurable) on an unsupported major. Defer *hello-side* negotiation to the P7 "`v` in the hello envelope" item; note `hello` is **optional** today (default `UseDefaults()` never reads one via `UseWebSocketHello<T>`), so the client sends it only when configured.

**Milestones.**
- **M1** — typed protocol: inbound DTOs + codegen + golden check.
- **M2** — `VoxaClient` happy path: connect → session → mic → transcription/text/speaking → playback (port `recorder-worklet.js`).
- **M3** — barge-in: `flushPlayback` on interruption / user-speaking (closes the P2 client half).
- **M4** — frontend tools: `onToolCall` / `sendToolResult` round-trip (absent from the test page today).
- **M5** — packaging: ESM + types, framework-agnostic core, an optional thin `useVoxa` React hook, and an `examples/` vanilla page that replaces the inline test HTML.

**Effort:** ~4–5 days (codegen + inbound DTOs ~1.5d, client core ~2d, tools + packaging ~1.5d).

**Open questions.** (a) codegen mechanism — reflection-based emitter vs hand-authored TS guarded by a golden diff; (b) ship `@voxa/react` now or just document the hook; (c) fold a typed `hello` (agentId / future resume token) into v0.1 or wait for P7 + the session-resilience item under P6.

Estimated effort (remaining P5): `@voxa/client` ~4–5d (**first**), then `Voxa` meta-package + `dotnet new voxa-server` template ~2d. `AddVoxa` shipped.

## P6 — Capability

- ✅ **Local/offline speech tier** — shipped as VLS-001 ([spec](docs/specifications/voxa-local-speech-spec.html), [guide](docs/local-speech.md)): `Voxa.Speech.WhisperCpp` (STT), `Voxa.Speech.Piper` (fast TTS), and `Voxa.Speech.Kokoro` (quality TTS) with a shared SHA-256-pinned model cache (first-run download / offline mode), a keyless `Echo` agent, startup model warm-up, and a zero-network CI conversation lane. Develop without API keys, deploy air-gapped, run zero-cost CI conversations.
- ✅ **Local Voxtral realtime STT (VLS-009)** — shipped ([spec](docs/specifications/vls-009-voxtral-realtime-stt-spec.html)): `Voxa.Speech.Voxtral` adds Mistral's open-weights **Voxtral-Mini-4B-Realtime** (Apache-2.0) as a fully-offline, cloud-grade STT served by a local **vLLM** server over its realtime WebSocket — connect-only or Voxa-managed. The streaming `ISpeechToTextEngine` talks the `append`/`commit`/`delta`/`done` envelopes directly (a plain channel, not the `WebSocketSttEngine` base); Studio GPU-gates it as the default (≥ 16 GB VRAM via an `nvidia-smi` seam) with whisper.cpp as the keyless fallback. Ships a dev mock realtime server. *Deferred:* a pinned auto-download bundle and a lighter CPU/GGUF tier (needs a GPU CI lane / upstream llama.cpp support).
- ✅ **Voice library & cloning** — shipped (cloud-first) as VVL-001 ([spec](docs/specifications/voxa-voice-library-spec.html), [guide](docs/voice-library.md)): two capability seams (`IVoiceCatalogProvider`, `IVoiceCloneProvider`) any TTS provider may implement; ElevenLabs and Mistral list voices live and clone from samples; **Voxtral** joins as a Mistral STT provider; Studio gains a **Voices** section — a reconciled library (Live/Stale/Discovered) and a consent-gated clone wizard — and Config's cloud voice picker feeds clones straight into a pipeline. *Deferred:* keyless **local** ONNX cloning (OpenVoice tone-color converter) pending a validated ONNX export + audio-quality spike; the wizard shows it as coming soon.
- ✅ **Studio Settings & persistent credentials** — shipped as VST-003 ([spec](docs/specifications/vst-003-settings-dialog-spec.html), [guide](docs/settings.md)): a tabbed **Settings** dialog (nav-rail gear) to activate providers and store their API keys, encrypted on Windows via **DPAPI** (`~/voxa-secrets.dpapi`) and live from the next launch. Manifests are role-keyed — one OpenAI key covers STT + TTS + the agent; Mistral covers STT + TTS. Keys are a dedicated config layer (a Config Apply can't wipe them) and never reach any export. Config dropdowns filter to activated-or-local providers. *Deferred:* an Audio settings tab, and a cross-platform encrypted backend (macOS Keychain / Linux libsecret).
- **Session resilience / reconnect** — a dropped mobile WebSocket shouldn't lose the conversation. The hello envelope gains an optional resume token; the host maps it to conversation state (AONIK already has `ChatThread.Id` for exactly this). On resume the sink replays the last unfinished bot turn.
- **Typed frontend tools** — source-generate the JSON schema from a C# delegate so tool calling becomes `voice.UseTool("show_chart", (string city, int days) => ...)` instead of hand-written `ArgumentsJson` plumbing; include an approval-required wrapper matching MAF's `ApprovalRequiredAIFunction`.
- **Multi-agent handoff** — switch the active `IAgentTurnDriver` (persona / department) mid-call on a control frame, preserving transport and VAD state.

## P7 — Operability & configurability

- ✅ **Per-stage latency waterfall** — shipped by VST-001 WS0: the per-turn breakdown (VAD close → STT final → LLM first token → TTS first byte → audio out) is derived inside `VoxaDiagnosticsHub` (a `StageLatencyTracker` over the hub's anchor events) and recorded as `voxa.stage.latency` histograms (tag `stage`), making `voxa.turn.ttfb` *diagnosable*. Enable with `Voxa:Diagnostics:Enabled`. Voxa Studio's Talk view renders it live; a `/voxa/debug` browser page over the same hub remains open for a follow-up.
- **Runtime control envelope** — client-sent `{"type":"configure", ...}` to adjust VAD thresholds / voice / language mid-session (mobile acoustic environments vary wildly), guarded by a host-side allowlist.
- **Conversation test harness** — extend `Voxa.Testing` with a scripted-conversation runner: WAV (or text) in → ordered transcript/frame expectations + latency budgets out, deterministic clock, runnable in CI. Also the cure for timing-flaky tests (e.g. the MicrosoftAgents shutdown test under parallel suite load). **Now specced as [VDX-009](docs/specifications/vdx-009-behavioral-evals-spec.html)** — declarative YAML scenarios against the real composed pipeline (text and synthesized-audio lanes, diagnostics-hub assertions, optional local LLM judge), with a starter pack that re-derives this quarter's three shipped conversation bugs.
- **Pipeline health watchdogs** — [VRT-007](docs/specifications/vrt-007-pipeline-health-watchdogs-spec.html): data-path heartbeats with a sink-side stall monitor (doubles as a whole-pipeline transit-latency probe), session idle detection with optional graceful auto-end (telephony hang-up), and teardown hygiene reports naming the processor that failed to stop. All off by default, zero new pipeline stages.
- **Wire protocol doc + versioning** — `docs/wire-protocol.md` generated from the `WireMessages` DTOs, plus a `"v": 1` field in the hello envelope so future protocol changes can be negotiated instead of breaking. *(The `session` envelope already carries `v` via `SessionInfoFrame.ProtocolVersion`; the DTO→TS generator and the inbound-envelope DTOs land with `@voxa/client` in P5 — this item adds the prose doc and **hello-side** negotiation on top.)*

## P8 — Voxa Studio (developer desktop app)

✅ **Shipped as VST-001** ([spec](docs/specifications/voxa-studio-spec.html), [guide](docs/studio.md)). An Avalonia desktop app (`apps/Voxa.Studio`, Windows-first audio behind an `IStudioAudioDevice` seam) that hosts the real composed pipeline in-process against the developer's mic and speakers:

- **Talk** — live conversation with a scrolling VAD probability trace and a per-turn stage-latency waterfall (the view that would have caught the Silero v5 context bug in seconds)
- **Voices** — audition / A/B the Piper and Kokoro catalogs with TTFB + RTF measured on local hardware
- **Models** — model-cache inventory, SHA-256 verify, prefetch-all (air-gap provisioning), purge
- **Config** — registry-driven pipeline composer that exports a validated `appsettings.json` block

Foundation: **WS0 `VoxaDiagnosticsHub`** — a per-session typed pipeline event stream (VAD windows, turn edges, transcripts, stage latencies) that is zero-cost when unobserved, ships in the framework (not the app), and also delivers the P7 latency-waterfall item for server hosts. Keyless out of the box thanks to the VLS-001 local tier.

Estimated effort: ~2.5 weeks (WS0 diagnostics ~4d, shell + audio ~4d, Talk ~3d, Voices/Models/Config ~4d, CI + docs ~2d).

**Next iteration — VST-002 ([design brief](docs/specifications/voxa-studio-design-brief.html)):** animated-mark launch experience + motion system, dedicated STT/TTS playgrounds (WER harness, take history, blind A/B/X), a node-style pipeline Builder on an interactive canvas (registry-driven palette, typed ports, run-from-canvas, live frame-flow visualization — chain-only until the runtime supports branching), and a Run & Metrics workbench (scripted repeatable runs, TTFB percentiles, per-stage trends, run comparison). Design-approved phases D1–D4, ~26 days total.

## Not planned (deferred from original Pipecat scope)

- WebRTC transports (Daily, LiveKit) — WebSocket is enough for mobile-first; if taken, a media-server
  integration rather than a .NET WebRTC stack
- ~~Telephony (Twilio, SIP)~~ — **Twilio shipped** via [VTL-001](docs/specifications/vtl-001-telephony-transport-spec.html)
  (`Voxa.Transports.Telephony` + `Voxa.Transports.Twilio`, `MapVoxaTwilioVoice`), over the existing
  WebSocket seam — no WebRTC. Azure ACS is a designed follow-up (§9); direct SIP stays out (it reintroduces
  the RTP/ICE stack telephony-over-provider exists to avoid)
- Vision / image / avatar processors
- Whisker / Tail debugging tools
- mem0, Sentry observers — use OpenTelemetry instead
