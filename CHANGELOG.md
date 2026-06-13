# Changelog

All notable changes to Voxa are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). Pre-1.0 the public API may change between minor versions.

## [Unreleased]

### Added

- **Voice picker hand-off + docs (VVL-001 WS6).** The Config composer gains a dynamic voice picker
  for cloud providers: selecting ElevenLabs or Mistral as TTS loads its voices live from the library
  (your clones included) and writes the provider-correct key into the export (`ElevenLabs:VoiceId` /
  `Mistral:Voice`) — never an API key. A keyless provider shows "key required" instead of an empty
  list. Piper/Kokoro keep their compiled-in catalog pickers. New guide
  [`docs/voice-library.md`](docs/voice-library.md); README and ROADMAP updated. (The GPL/license gate
  needs no extension yet — local cloning, which would add a package, is deferred.)
- **Voxa Studio: Voices section (VVL-001 WS5).** A new top-level *Voices* nav section (between
  Playgrounds and Builder) — the managed voice library. A grid of every voice the pipeline can use,
  each tagged `Live` / `Stale` / `Discovered` / `LocalCatalog`, with per-provider status chips (a
  keyless provider reads "key required" rather than showing empty). The headline is a **clone
  wizard** with a real consent gate: the Create command stays disabled until a name, ≥1 reference
  sample, a target provider, and an explicit "I have the right to clone this voice" attestation are
  all present — a successful clone persists a `VoiceProfile` stamped with `ConsentAttestedAt` and the
  reference clips; a provider rejection (plan-gated / no key) shows its message and saves nothing.
  Cloud cloning (ElevenLabs/Mistral) is fully wired; local ONNX cloning is shown as "coming soon"
  (deferred, WS3). Recording a sample is blocked while a Talk/Builder/Metrics run holds the audio
  device, and the library refreshes after a Config Apply. Audition deep-links to the TTS playground.
- **Voice library store + reconciliation (VVL-001 WS4).** A key-free on-disk library in Voxa Studio:
  `VoiceProfile` (a pointer to a provider voice plus local provenance — provider, remote id, the
  user's own reference clips, a consent timestamp; never a secret) persisted one JSON-per-profile
  under `~/voxa-voices` by `VoiceStore` (folder scan, corrupt-skip, ctor override as the test seam —
  the `RunStore` pattern). `VoiceCatalogService` is the picker's single source of truth: for each TTS
  provider it merges the compiled-in catalog (Piper/Kokoro), the provider's live `ListVoicesAsync`,
  and saved profiles, reconciling each into `Live` / `Stale` / `Discovered` / `LocalCatalog` (a
  voice deleted server-side shows `Stale`, never silently usable), with a short (60 s) list cache and
  graceful degrade — a provider with no key surfaces its profiles plus a `MissingKey` flag rather
  than crashing.
- **ElevenLabs & Mistral voice catalogs + cloning, Voxtral STT (VVL-001 WS1–WS2).** Both cloud TTS
  packages now implement the WS0 capability seams: `ElevenLabsVoiceCatalog` (`GET /voices`, instant
  clone via `POST /voices/add`, `DELETE /voices/{id}`) and `MistralVoiceCatalog` (`/v1/audio/voices`)
  list and create voices live; a blank key surfaces as a typed `VoiceProviderException(MissingApiKey)`
  and a provider rejection (e.g. plan-gated cloning) carries the provider's message rather than a raw
  `HttpRequestException`. `Voxa.Speech.Mistral` also gains **Voxtral STT** — a new `Mistral` STT
  provider (`MistralSpeechToTextEngine`, utterance-buffered: it posts the whole utterance to
  `/v1/audio/transcriptions` on speech-end and yields one final transcript), registered by the
  meta-package beside Mistral TTS, so Studio's STT dropdown lists it automatically.
- **Voice capability seams (VVL-001 WS0).** Two optional, capability-based framework interfaces a
  TTS provider may implement — `IVoiceCatalogProvider` (list a provider's voices live) and
  `IVoiceCloneProvider` (create/delete a voice from samples) — plus the `ProviderVoice` /
  `VoiceCloneRequest` records, all in `Voxa.Speech.Abstractions` (`Voxa.Speech.Voices`). A
  provider opts in by setting `ResolveCatalog` / `ResolveCloner` on its `VoxaTtsDescriptor`;
  providers whose voices are a compiled-in list (Piper, Kokoro) leave them null. The registry
  exposes `TryGetVoiceCatalog` / `TryGetVoiceCloner`, which take the caller-supplied captured
  `"Voxa"` config section so they never service-locate `IConfiguration` (works on a plain
  `ServiceCollection` host). Foundation for the Studio voice library and cloning (VVL-001).
- **Voxa Studio: Run & Metrics workbench (VST-002 D4).** A new top-level *Metrics* section that
  turns sessions into evidence. A **run** = one configuration × one input source → a JSON
  bundle under `~/voxa-runs` (config snapshot without secrets, recorded event stream, computed
  stats, machine context — cores/OS/model-cache state, the R4 mitigation). Three sources: live
  mic, single WAV, or a **scripted deck** — utterance WAVs replayed turn-paced through the
  exact session machinery Talk uses (a `ScriptedAudioDevice` implements the device contract, so
  there is no parallel code path); scripted runs end themselves. During a run only a compact
  header updates; on completion the workbench renders the TTFB percentile card (nearest-rank
  p50/p95/max, TTS chunk-span RTF, delta vs the previous run), per-turn stage stacks, and a
  per-stage trend — all in the §3.3 stage palette — plus one rule-based **takeaway** sentence
  naming the dominant stage and a real knob. **Compare** any two runs (older = baseline) with
  context warnings when machines or cache states differ; per-turn CSV export. Cross-nav:
  clicking a stage block in Talk's waterfall deep-links to that stage's trend in Metrics. Talk,
  Builder, and Metrics runs block each other (one audio device, one set of cores — concurrent
  sessions would skew the numbers). Fenced out: text-injected (post-STT) scripted turns wait
  for a framework-level injection seam; Builder-graph runs record through the Builder itself.
- **Voxa Studio: Pipeline Builder (VST-002 D3).** A new top-level *Builder* section — a node
  canvas over the live provider registry. The palette generates from registered STT/TTS/VAD
  providers plus the built-ins (TranscriptionFilter, SentenceAggregator, Echo/OpenAI agent);
  ports are typed by frame kind (the stage palette), incompatible wires snap back with the
  reason in words, and a dangling port's **+** offers only type-compatible follow-ups. The
  canvas enforces single-in/single-out wiring — Voxa pipelines are a linear chain, and the
  geometry stays honest about that (§8.3). **Run graph** compiles the drawn chain through the
  same descriptors `UseDefaults()` uses, inside an ephemeral container layered over the live
  config, and runs it against the mic/speakers; live mode renders real hub events only — edge
  pulses per final transcript/agent delta/TTS chunk, gate-open shimmer, stage-node glow with
  measured latency, per-node queue depth, and a last-turn waterfall strip. **Export** produces
  the `appsettings.json` block when the chain matches the default shape, or generated C#
  composition code when it doesn't. Canvas furniture: drag + snap-to-grid, Tidy auto-layout,
  Ctrl+wheel zoom, undo/redo, save/load as JSON in the user profile. The Config view gains
  **Open in Builder** (the §5 cross-navigation), and the canvas opens with the active config
  as a graph. Out of scope by design (the R1 fence): no branching, groups, comments, subgraphs,
  or minimap; explicit DiagnosticsTap palette nodes wait for configurable taps in the framework
  (the builder auto-instruments the four stage taps instead).
- **Brand reach (VST-002 open question #4, resolved: yes).** The animated mark extends beyond the app: the README and the new docs index ([docs/README.md](docs/README.md)) carry a CSS-animated SVG of the mark (the splash's draw-on choreography, plays once, honors `prefers-reduced-motion`, dark/light variants via GitHub's theme switcher), and every NuGet package under `src/` now ships a `PackageIcon` — a 128 px raster of the app icon generated from code (`VOXA_BRAND_EXPORT=1` regenerates it) and wired through a shared `src/Directory.Build.props`.
- **Voxa Studio: STT and TTS playgrounds (VST-002 D2).** The nav's *Voices* section grew into
  *Playgrounds* — two standalone labs behind a segmented switch. The **STT lab** drives
  `WhisperCppSttEngine` directly against the bundled `jfk.wav` fixture, a dropped/browsed WAV
  (stereo + arbitrary-rate PCM16 converted), or a live mic recording; each utterance lands as a
  card stamped with its waveform and final-transcript latency, a reference text yields a live
  **WER** with insert/substitute/delete diff coloring, and side-by-side mode runs two models
  sequentially over the same audio. The **TTS lab** (the v1 Voice Lab, matured) adds a
  replayable take history whose waveform is the playback scrubber, an A/B/X blind test, a
  curated stress-phrase deck, and a batch bench producing TTFB p50/p95 + mean RTF per checked
  voice with CSV export. New shared `WaveformStripControl` (bottom-aligned envelope bars,
  optional interactive playhead) and an exact Levenshtein word-alignment `WordErrorRate`.

### Fixed

- **Kokoro: `bf_emma` and `bm_george` failed SHA-256 verification on download.** The two British-voice pins in `KokoroCatalog` never matched what `onnx-community/Kokoro-82M-v1.0-ONNX` serves (only download-time verification could catch it: the LocalModels CI lane runs against a pre-seeded cache, and the suite exercises the American voices). Re-pinned from the repo's authoritative LFS metadata and verified against fresh downloads.
- **Studio: one bad artifact no longer aborts "Prefetch full catalog".** Bulk provisioning now fetches each artifact independently and reports the casualties at the end ("Prefetched 21/23 — failed: …") instead of cancelling the remaining downloads on the first failure. Talk-session prefetch is unchanged — a session genuinely needs all of its artifacts.
- **Frames could be silently dropped at the processor handoff when an interruption raced them.** `FrameProcessor.QueueFrameAsync` passed the per-frame preemption token to `Channel.WriteAsync`, which checks the token *before* writing even when the channel has capacity — so a concurrent `InterruptionFrame` could abort the forwarding of an already-processed frame between processors (observed as a final transcript lost ~50% of the time when an interruption arrived immediately after it; the sink-side twin of this bug was fixed in VPS-001). The handoff now uses a synchronous `TryWrite` fast path, making it atomic with respect to preemption; the cancellable awaited write remains only for genuine backpressure on a full channel. `WebSocketAudioSinkPurgeTests.NonAudio_IsNeverPurged` — previously misfiled as a flaky test — pinned this bug all along and now passes deterministically.
- Deflaked `TextToSpeechProcessorTests`: replaced fixed `Task.Delay` waits with condition polling, and removed an ordering assertion between `TextFrame` (data channel) and `BotStartedSpeakingFrame` (system/priority channel) that the dual-channel architecture never guaranteed — system frames may legitimately overtake data frames. The meaningful FIFO contract (text envelope before the first audio chunk) is still asserted. Raised the WebSocket purge tests' wait cap from 3 s to the repo-standard 10 s.

### Added

- **`Voxa.Services.OpenAIRealtime`** — new package. `OpenAIRealtimeProcessor` is a composite STT+LLM+TTS+VAD processor backed by the OpenAI Realtime API. Full-duplex WebSocket session, server-side voice activity detection, native interruption — sub-400 ms turns. The C# equivalent of Pipecat's `OpenAIRealtimeBetaService`. Use this instead of chaining `Voxa.Speech.OpenAI`'s Whisper + TTS engines when you want low-latency conversational voice without hallucinations on silence.
  - 9 unit tests cover session.update emission, audio buffer append, audio/transcript delta translation, interruption-on-bot-speaking, function calls, and error propagation.
- **`TranscriptionFilter`** (in `Voxa.Speech.Abstractions`) — drops final `TranscriptionFrame`s that match Whisper's well-known silence/breath hallucinations ("Thank you.", "Bye.", ".", "you", "Subscribe", etc.) plus user-tunable exact and substring blocklists, plus a minimum-length check. Critical when chaining Whisper REST with anything that would synthesise the bogus text. 5 unit tests.
- **`SentenceAggregator`** (in `Voxa.Speech.Abstractions`) — buffers `LlmTextChunkFrame`s coming from a streaming LLM and emits whole-sentence `TextFrame`s downstream as soon as a sentence boundary lands. Lets a downstream `TextToSpeechProcessor` start synthesising the first sentence while the LLM is still generating the rest — Pipecat's `SentenceAggregator` pattern. Eager flush at end-of-buffer + drop on `UserStartedSpeakingFrame` interruption + leftover flush on `EndFrame`. 7 unit tests.
- **`SileroVadOptions.PrerollDuration`** (default 300 ms) — `SileroVadProcessor` now keeps a rolling buffer of the last N audio windows and replays them downstream the moment the gate opens. Without this, the first 200 ms of every utterance was silently dropped (matching `StartDuration`), so the leading consonant of every sentence was lost on the way to STT. Matches Pipecat's `speech_pad_ms`.
- Sample app: new **`/voice/openai-batch`** route — Whisper STT + `gpt-4o-mini` chat + OpenAI TTS, wired through `SileroVadProcessor` (or `SilenceGateProcessor`) + `TranscriptionFilter` + `MicrosoftAgentsProcessor` + `SentenceAggregator`. Designed to feel close to Realtime at ~45× lower cost (≈ $0.40/hr vs $18/hr).

### Changed

- **`TextToSpeechProcessor` now forwards the input `TextFrame` / `LlmTextChunkFrame` downstream BEFORE synthesizing audio.** Mirrors Pipecat's TTS pattern. Transports / UI sinks can now render the spoken text in real time as the bot speaks. The previous behaviour was to silently consume the text frame, which left WebSocket clients with empty conversation bubbles. Two new tests pin the ordering: text frames arrive before `BotStartedSpeakingFrame` and the first `AudioRawFrame`.
- Sample app's demo dropdown now leads with the cost-effective `OpenAI Whisper + gpt-4o-mini + TTS (cheap)` preset, with `OpenAI Realtime (premium)` as the second option. Old echo-only OpenAI route remains as `OpenAI Whisper + TTS (echo only)` for diagnostics.

## [0.3.0-alpha] - 2026-05

### Added

- **`Voxa.Audio.SileroVad`** — new package. ML-based voice activity detection using the bundled Silero VAD v5 ONNX model (~2.3 MB embedded resource, MIT-licensed). Same emission contract as `SilenceGateProcessor` (`UserStartedSpeakingFrame` / `UserStoppedSpeakingFrame` on transitions) so it's a drop-in upgrade. Handles keyboard noise, fans, distant chatter, music — anything `SilenceGateProcessor`'s energy threshold gets confused by.
  - `SileroVadEngine` — thin stateful wrapper around the ONNX model. Supports 16 kHz (512-sample windows) and 8 kHz (256-sample windows). LSTM hidden state persists across calls.
  - `SileroVadProcessor` — `FrameProcessor` with hysteresis (separate activation / deactivation thresholds) and minimum-duration rules (default 64 ms speech-on / 256 ms speech-off).
  - 11 unit tests cover construction, sample-rate validation, silence classification, and processor pass-through behaviour.

### Changed

- Sample app's granular routes use `SileroVadProcessor` by default. Override with `Voxa:Vad=Silence` (energy gate) or `Voxa:Vad=None` (no VAD) in `appsettings.json` / user-secrets.

## [0.2.0-alpha] - 2026-05

### Added

- **`Voxa.Speech.Abstractions`** — new package. Hoists `ISpeechToTextEngine` / `ITextToSpeechEngine` and the generic `SpeechToTextProcessor` / `TextToSpeechProcessor` out of the Azure-specific package so any vendor can plug in.
- **`Voxa.Speech.OpenAI`** — new package. `OpenAIWhisperEngine` (REST `/v1/audio/transcriptions`, buffer-and-flush) and `OpenAITextToSpeechEngine` (REST `/v1/audio/speech`, streaming PCM). Configurable base URL for OpenAI-compatible proxies.
- **`Voxa.Speech.ElevenLabs`** — new package. `ElevenLabsTextToSpeechEngine` over the streaming TTS endpoint. Voice cloning, voice settings (stability, similarity, style, speed, speaker boost), regional endpoints.
- **`Voxa.Speech.Mistral`** — new package. `MistralTextToSpeechEngine` for Mistral's Voxtral-TTS via the OpenAI-compatible `/v1/audio/speech` endpoint.

### Changed

- **`Voxa.Services.AzureSpeech` renamed to `Voxa.Speech.Azure`** to match the new vendor naming convention (`Voxa.Speech.<Vendor>`). Engines are unchanged; processor classes are now the generic ones from `Voxa.Speech.Abstractions`. Old package on nuget.org stops at v0.1.0-alpha.2; consumers should switch to `Voxa.Speech.Azure`.

### Migration

```csharp
// Before (v0.1.x)
.Then(new AzureSpeechSttProcessor(speechOpts))
.Then(new AzureSpeechTtsProcessor(speechOpts))

// After (v0.2.x)
.Then(AzureSpeech.StreamingTranscription(speechOpts))
.Then(AzureSpeech.Synthesis(speechOpts))
// or, longhand:
.Then(new SpeechToTextProcessor(new AzureSpeechToTextEngine(speechOpts)))
.Then(new TextToSpeechProcessor(new AzureTextToSpeechEngine(speechOpts)))
```

## [0.1.0-alpha.2] - 2026-05

### Added
- Per-package READMEs included in every NuGet — proper landing pages on nuget.org for `Voxa.Testing`, `Voxa.Transports.WebSocket`, `Voxa.Services.AzureVoiceLive`, `Voxa.Services.AzureSpeech`, `Voxa.Services.MicrosoftAgents`, `Voxa.Observability`, and an updated `Voxa.Core` README that's package-specific rather than the repo root.

### Fixed
- WebSocket sink and AzureVoiceLive tests now use polling helpers instead of fixed `Task.Delay`s — eliminates the timing flakes seen on slower Linux CI runners.

### Changed
- CI and release workflows opt into `FORCE_JAVASCRIPT_ACTIONS_TO_NODE24` so the `actions/checkout`, `actions/setup-dotnet`, and `actions/upload-artifact` steps run on Node 24 (silences the Node 20 deprecation warning).

## [0.1.0-alpha] - 2026-05

### Added

#### Phase 1 — Core pipeline primitives
- `Frame` record hierarchy: `DataFrame`, `ControlFrame`, `SystemFrame`, plus 14 concrete frames (audio, transcription, text, tool calls, start/end/heartbeat, interruption, speaking events, error).
- `FrameProcessor` base class with two-task drain (system priority + data) and per-frame `CancellationToken` so interruptions preempt long-running data frames.
- `Pipeline` + fluent `PipelineBuilder`, `PipelineSource`/`PipelineSink`, `PipelineRunner` with `StartAsync`/`StopAsync`/`WaitAsync` lifecycle.
- `PipelineSink.EndFrameObserved` Task — runner observes graceful shutdown without competing with sink consumers.

#### Phase 2 — Voice Live + offline harness
- `Voxa.Services.AzureVoiceLive`: composite `AzureVoiceLiveProcessor` speaking the Realtime API protocol (works for Azure Voice Live, Azure OpenAI Realtime, and OpenAI Realtime).
- `IRealtimeApiTransport` + `WebSocketRealtimeApiTransport`, `RealtimeEventCodec`.
- `Voxa.Testing` package: `WavFile`, `WavFileSourceProcessor`, `WavFileSinkProcessor`, `CapturingProcessor`, `PassthroughProcessor`.

#### Phase 3 — Transport + agents
- `Voxa.Transports.WebSocket`: host-agnostic `WebSocketAudioSource`/`WebSocketAudioSink` over any `System.Net.WebSockets.WebSocket`. Wire protocol: binary PCM + typed JSON envelopes.
- `Voxa.Services.MicrosoftAgents`: `MicrosoftAgentsProcessor` wraps any Microsoft Agent Framework `AIAgent`. Targets `Microsoft.Agents.AI` 1.5.0.

#### Phase 5 — Granular Speech + sample
- `Voxa.Services.AzureSpeech`: `AzureSpeechSttProcessor` + `AzureSpeechTtsProcessor` backed by the Cognitive Services Speech SDK. `ISpeechToTextEngine` / `ITextToSpeechEngine` abstractions for testability.
- `Voxa.Samples.AspNetServer`: runnable ASP.NET Core voice-agent server composing the full Voxa stack.

#### Phase 6 — Observability + release prep
- `Voxa.Observability`: `TracingProcessor` and public `VoxaActivities.Source` (`ActivitySource` named `Voxa`) for OpenTelemetry integration.
- GitHub Actions CI workflow (build + test on push/PR).
- GitHub Actions release workflow (NuGet publish on git tag).
- `CONTRIBUTING.md`, `CHANGELOG.md`.

### Fixed
- Consuming processors (Voice Live, Azure Speech STT/TTS, Microsoft Agents) now forward `StartFrame`/`EndFrame` and other unrecognised frames downstream so the sink's `EndFrameObserved` fires and `runner.WaitAsync()` completes on graceful stop.

[Unreleased]: https://github.com/michaeljosiah/voxa/compare/v0.3.0-alpha...HEAD
[0.3.0-alpha]: https://github.com/michaeljosiah/voxa/compare/v0.2.0-alpha...v0.3.0-alpha
[0.2.0-alpha]: https://github.com/michaeljosiah/voxa/compare/v0.1.0-alpha.2...v0.2.0-alpha
[0.1.0-alpha.2]: https://github.com/michaeljosiah/voxa/compare/v0.1.0-alpha...v0.1.0-alpha.2
[0.1.0-alpha]: https://github.com/michaeljosiah/voxa/releases/tag/v0.1.0-alpha
