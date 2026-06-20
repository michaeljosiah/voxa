# Changelog

All notable changes to Voxa are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). Pre-1.0 the public API may change between minor versions.

## [Unreleased]

### Added

- **Shared ONNX Runtime model host (VLS-006).** A new **`Voxa.Audio.Onnx`** package ends the per-engine ORT
  session setup that SileroVad and Kokoro each re-implement today: `OnnxModelHost.Load(path, device, hook)`
  returns a shared `IOnnxSession` from a **process-wide `(path, device)` cache** (weights load once, shared by
  every connection — the Kokoro/whisper.cpp cache shape, lifted), with `EvictAll()` for tests/Studio. A device
  seam (`OnnxDevice` + `OnnxDeviceParser`, the shared `cpu`/`auto`/`cuda`/`directml`/`coreml` `Device`
  convention) keeps **CPU the default and byte-identical** — the package references only the CPU
  `Microsoft.ML.OnnxRuntime` (`1.26.0`, `PrivateAssets`, pinned to SileroVad/Kokoro), so **GPU is opt-in via a
  user-added `Microsoft.ML.OnnxRuntime.Gpu`/`.DirectML` package and never bundled**; an explicit GPU device
  without its runtime fails at session creation with a copy-pasteable remediation, while `auto` falls back to
  CPU with a warning (never a throw). An `OnnxModelDescriptor` (graph + SHA-256-pinned sidecars) + `ResolveAsync`
  lets a model self-describe and resolve through the unchanged `VoxaModelCache`. The package ships the **host +
  seam + descriptor shape, not a model**: per-family catalogs (and their pinned hashes + cleared licences) ride
  the consuming engines (VLS-004/005/007/008, VRT-005). Existing engines are unchanged — adopting the host is an
  additive follow-up. The `OnnxTensors` convenience run-helper is deferred to the first consumer that pins its
  dtype matrix.
- **Speaker diarization — seams + pure-C# pipeline (VLS-005 WS1).** A new **`Voxa.Audio.Diarization`** package
  adds *"who spoke when"* to fill the long-present but always-null `TranscriptionFrame.SpeakerId`. It ships the
  seams — `ISpeakerSegmentation` (audio → speech regions) and `ISpeakerEmbedding` (a span → a speaker vector),
  both model-backed — and the orchestrating `IDiarizer` whose reference implementation, **`DiarizationPipeline`**,
  does everything else in **pure C# with no ML runtime** (and no `Voxa.Core` dependency): form regions → embed →
  **constrained agglomerative clustering by cosine distance (centroid linkage, matching speech-core's 0.715
  calibration)** → stable 0-based speaker ids, with consecutive same-speaker regions merged into one turn. `DiarizerConfig` exposes the knobs (default to speech-core's values), the
  load-bearing one being `ClusteringThreshold` (cosine-distance merge ceiling, `0.715`); `MinSpeakers` /
  `MaxSpeakers` (`0` = auto) force a floor / cap past the threshold. Because the clustering takes `float[]`
  embeddings and emits segments with no I/O, it's exercised on **synthetic embeddings** in the default lane
  (N tight clusters → N speakers, threshold sensitivity, speaker-count caps, label stability, determinism). The
  reference **ONNX impls** (Pyannote segmentation + WeSpeaker embedding, on the VLS-006 host) and the
  `voxa transcribe --diarize` CLI consumer are deferred follow-ups — they need real pinned models (SHA-256 +
  cleared licences). Real-time diarization and speaker *identification* are out of scope.
- **Local speech-enhancement / denoise seam (VLS-004 WS1).** A new `IAudioEnhancer` (in `Voxa.Audio.Abstractions`)
  + `NullAudioEnhancer` passthrough + `AudioEnhancerProcessor` that runs the denoiser per `AudioRawFrame`, placed
  by the composer **after the AEC stage (VRT-003) and before the VAD** so the VAD and STT see the cleaned mic
  signal. A `VoxaEnhancerDescriptor` + `TryGetEnhancer` registry slot + public
  `VoxaBuilder.AddProvider(VoxaEnhancerDescriptor)` let a real engine drop in by config; the composer inserts the
  stage from `Voxa:Enhance:Engine` (default `None` ⇒ byte-identical, golden-tested) behind a fail-fast validator.
  Ships the **seam, not a model** — the reference DeepFilterNet3 ONNX engine (WS2) is deferred pending license
  verification and a SHA-256-pinned artifact (the program's "define the seam, defer the heavy model" discipline).
- **Streaming STT — interim-transcript coalescing (VRT-004 WS1).** Interim (`IsFinal:false`) transcripts already
  propagate end-to-end for streaming engines and the agent already ignores them; VRT-004 adds
  `SpeechToTextProcessor.InterimMinInterval` (config `Voxa:InterimMinIntervalMs`, default ~150 ms) which throttles
  interim frames to at most one per window, so a chatty engine can't flood the bounded data channel. Finals are
  never coalesced and interims are rate-limited (never gated off). Regression tests lock the two invariants —
  interims keep flowing, and the agent fires exactly once, on the final. The Azure event-mapping pin (WS2) and
  Studio's live-caption surface (Phase C) are follow-ups.

- **Acoustic echo cancellation seam (VRT-003).** A new zero-extra-dependency **`Voxa.Audio.Abstractions`**
  package defines `IEchoCanceller` (`FeedReference(farEnd)` / `CancelEcho(nearEnd)` / `Reset()` / `SampleRate`)
  — the plug a real DSP fits into for **barge-in over speakers**, where the bot's own audio loops back into the
  mic. It ships the seam, not the DSP: a `NullEchoCanceller` passthrough, an `EchoCancellerProcessor` placed
  *before* the VAD (cleans each mic frame; resets on session start and on an interruption epoch), and an
  `EchoReferenceTapProcessor` placed *after* TTS that feeds the bot's outbound audio as the far-end reference
  (transport-agnostic — the sink is untouched). A `VoxaAecDescriptor`/`VoxaAecSettings` pair + a `TryGetAec`
  registry slot mirror the VAD provider model, and the composer inserts the stage + tap from `Voxa:Aec:Engine`
  (default `None`), guarded by a fail-fast validator. With it unset/`None` the composed pipeline is
  **byte-identical** to before (no stage, no tap — golden-tested). A production canceller (WebRTC APM / SpeexDSP)
  is a separate opt-in `Voxa.Audio.Aec.*` follow-up.
- **Eager / speculative STT (VRT-002 WS1).** A `SileroVadOptions.EagerSttDelay` (`< StopDuration`) lets the VAD
  emit a marked `SpeculativeUtteranceFrame` once silence reaches that delay, so STT starts transcribing the
  buffered utterance **before** the full end-of-turn hangover elapses — the transcript is often ready by the time
  the turn is confirmed. If the user resumes within the window, or a smart-turn `ConfirmTurnEnd` returns `false`,
  the speculative utterance is **superseded**: `SpeechToTextProcessor` drops its final before it becomes a
  `TranscriptionFrame` (the guarantee — the per-frame cancellation token never reaches STT inference, so
  suppression by id, not cancellation, is what discards it). On a confirmed end-of-turn the speculative pass is
  *promoted* (no second STT pass). The contract change is minimal and additive: `TranscriptionResult` gains an
  optional `UtteranceId`, and `ISpeechToTextEngine` gains defaulted `FlushAsync(long)` + `DiscardBufferedAudioAsync()`
  hooks (whisper.cpp implements peek-without-clear + discard so a resume re-transcribes the full merged utterance).
  Off by default; on in `LowLatency`/`Cheap` (`Voxa:Vad:EagerSttDelayMs`).
- **Turn-taking robustness knobs (VRT-002 WS2).** Three opt-in, default-off guards against pipeline-wedging edge
  cases: **empty/low-confidence STT recovery** (an empty final no longer leaves the agent loop waiting on a turn
  that never comes — it's forwarded and the worker stays ready), **`MaxUtteranceDuration`** force-split (a
  non-pausing speaker / stuck-open mic is split into periodic intermediate transcripts instead of buffering
  forever — `Voxa:Vad:MaxUtteranceDurationMs`), and **`MaxResponseDuration`** (a runaway/looping LLM turn is
  truncated and closed cleanly, `LlmTurnEndedFrame` still fires — `Voxa:Agent:MaxResponseDurationMs`). All wired
  through the profiles (on in `LowLatency`/`Cheap`, off in `Default`/`Quality`); the `Default` profile keeps every
  knob off so the composed pipeline stays byte-identical. The two *interruption* knobs from the spec
  (`MinInterruptionDuration` debounce, `InterruptionRecoveryTimeout`) are intentionally deferred to a dedicated
  follow-up — both require bot-speaking awareness at the VAD plus the sink's barge-in epoch interplay, which the
  spec isolates as its highest-risk change.
- **Turn-taking quality benchmark (VRT-001).** A new `bench/Voxa.TurnTaking` harness drives the **real
  composed pipeline** (`DefaultVoicePipelineComposer.Compose`) through a Full-Duplex-Bench-layout corpus and
  reduces the existing `VoxaDiagnosticsHub` stage timings to a per-sample JSON record + response WAV — no new
  runtime instrumentation, no parallel pipeline, so the numbers equal what production reports. It rolls those
  up to a per-category `summary.csv` (p50/p90/p99) and a direction-aware `score.json` — a turn-offset-rate
  for `pause_handling` (lower is better; silence through a thinking-pause is the win) and first-word latency
  for `smooth_turn_taking` / `user_interruption` — then diffs against a checked-in `baseline.json` so a
  turn-taking regression **fails the build**. `backchannel` is discovered and skipped (N/A for a half-duplex
  cascade), never faked. The default lane is offline + deterministic (mock STT/TTS + the keyless Echo agent
  over a tiny checked-in mini fixture of real `jfk.wav`-derived audio) and gates in CI via an xUnit smoke
  test; real local engines (WhisperCpp/Kokoro) run behind `Category=LocalModels`. This is the measurement
  foundation the rest of the VRT line (eager STT, barge-in/AEC, streaming captions) is gated against.
- **Smart turn detection (P0 latency).** A within-sentence pause no longer has to end the turn. A new
  `ISmartTurnClassifier` seam (in `Voxa.Speech.Abstractions`) is wired through the VAD
  (`VoxaVadSettings.ConfirmTurnEnd` → `SileroVadOptions`) and the `DefaultVoicePipelineComposer`, which
  auto-wires any registered classifier (zero-cost when none is). A new opt-in **`Voxa.Audio.SmartTurn`**
  package ships `AddVoxaSmartTurn(configuration)` and two classifiers, so the VAD asks "is the user
  actually done?" at the silence timeout and `Voxa:Vad:StopDurationMs` can drop to ~200 ms without clipping
  speakers who pause to think:
  - `Provider: "Http"` (`HttpSmartTurnClassifier`) — POST the recent speech to any smart-turn endpoint.
  - `Provider: "Sidecar"` (`SidecarSmartTurnClassifier`) — run the real `pipecat-ai/smart-turn-v3` model
    in a Voxa-managed **Python sidecar** (bundled `sidecar/voxa_smart_turn_sidecar.py`), so the model's
    Whisper feature extraction runs natively rather than as a fragile C# port. Lazy launch, a readiness
    handshake with bounded startup + per-turn timeouts (so a loading/hung sidecar never stalls the turn),
    stderr→log, auto-relaunch; fails "complete" on any error.

  Smart turn stays **opt-in** and Python-free unless you choose the sidecar: the core, the pipeline, and
  the local speech tier need no interpreter, and the HTTP path needs no *local* Python. The in-process
  ONNX classifier (no network, no Python on the turn path) is the documented next step. See the README.
- **Voxa Studio: Smart turn detection toggle (Config).** The Config view gains a **Smart turn detection**
  card — flip it on, pick a classifier (**Sidecar**, the real local `pipecat-ai/smart-turn-v3` via Python,
  or **Http**, a model server), and the next Talk session asks "is the user actually done?" at the silence
  timeout instead of ending the turn on raw silence. Off by default and zero-cost (no classifier
  registered). A half-filled form stays inert (it won't fail-fast), and the choice flows through the same
  Apply/Export path as the rest of the pipeline. The bundled sidecar script now resolves relative to the
  app, so the in-app default works without fuss.
- **Voxa Studio: tidier Builder toolbar.** The two export buttons collapse into one **Export ▾** dropdown
  (appsettings / C# compose), and Save collapses into one **Save ▾** dropdown — *Save to active profile*
  plus a *Save as a new profile* name field — reclaiming the always-on text box and three buttons.
- **Voxa Studio: Models tabs + provider filters (Models & Voices).** The **Models** page now groups the
  cache into tabs — **All / STT / TTS / Other** — and each tab has a **provider** dropdown (Whisper /
  Piper / Kokoro, scoped to what's in that tab) to narrow the list. The **Voices** library gains the same
  **provider** filter in its header, so you can focus on one provider's voices. Both filter live and reset
  sensibly when the tab/library changes.
- **Voxa Studio: app icon.** The Windows executable and taskbar/title-bar now show the VOXA mark instead
  of a blank default icon — a multi-resolution `voxa.ico` (16–256 px) generated from the brand geometry
  (`tools/voxa-icon-gen.cs`), wired via `ApplicationIcon` and the windows' `Icon`.
- **Voxa Studio: Builder "Save" updates the active profile.** Editing a pipeline and saving it back to the
  selected profile no longer needs a re-typed name — a **Save** button updates the active profile in place
  and re-applies it live. Selecting a profile in the Pipeline Profile bar now also loads it onto the Builder
  canvas (undoable), so *select → edit → Save* round-trips. "Save as new" still creates a fresh named profile.
- **Voxa Studio: named pipeline profiles, app-wide (Builder Phase 2).** Save a pipeline you've built as
  a named **profile** and switch the whole app to it from one place. A new **Pipeline Profile** bar sits
  above every view: pick a profile and it's applied everywhere at once — Talk, the Playgrounds, the lot —
  via the same live-reconfigure the Config Apply uses; the choice persists, so Studio reopens on the
  pipeline you left. Save one from the **Builder** ("Save as profile", for default-shape chains) — it
  appears in the bar and becomes active. Profiles store only the provider/model selection, **never API
  keys** (those stay in the encrypted secrets layer), so the `~/voxa-pipelines.json` file is safe to
  share. A raw Config **Apply** still works and simply shows the bar as "Custom".
- **Voxa Studio: Talk feels alive — pipeline state, warm-up, smoother render.** Talk used to look
  frozen while it worked. A prominent **status pill** now shows the live pipeline state — Warming up →
  Listening → Hearing you → Transcribing → Thinking → Speaking — derived from the diagnostics hub, so
  you always know what's happening (and, crucially with half-duplex on speakers, exactly when the mic is
  live: *Listening* = your turn). It debounces the per-sentence TTS edges so it doesn't flicker mid-reply.
  Cached models are now **warmed up during the launch splash** (and again after a Config Apply), so a
  returning user's first turn is instant — whisper.cpp caches its factory process-wide, so the live
  session reuses what the splash warmed (Start re-warms only as a safety net). Warm-up is **cached-only**
  off the splash: first-run still downloads at your first Start, with progress — no network before you
  act. The active **pipeline** (VAD · STT · agent · TTS · voice) is shown as an always-visible chip.
  And the VAD-trace render is throttled to ~12 fps, cutting per-frame allocations and stutter.
- **Voxa Studio: Builder reliability + validation (Phase 1).** The Pipeline Builder canvas was flaky
  and easy to leave in a broken state. Node dragging now captures the pointer, so a fast drag no longer
  stalls or strands the gesture (it matched wire-dragging's behavior, which already captured). A new
  **Reset** button returns the canvas to the default pipeline (undoable). Validation is now visual and
  enforced: the status strip shows a green/red state dot and lists **every** reason a chain is invalid
  (not just the first), the offending nodes ring **red** on the canvas, and **Save is disabled until the
  chain is valid** — you can no longer save an incomplete pipeline. (Named, app-wide pipeline profiles —
  save/load and per-page selection — are the planned Phase 2.)
- **Voxa Studio: half-duplex echo suppression + local-provider config (VST-004 polish).** Talk now
  gates the mic while the bot is speaking (plus a short hangover), so a user on **speakers** no longer
  loops the bot's own output back through VAD → STT → agent — the "it keeps repeating what I said"
  feedback loop. Half-duplex is the default; set `Voxa:Studio:AllowBargeIn=true` for full-duplex
  barge-in (use headphones). The Config tab also surfaces two providers that already existed in the
  pipeline: **Ollama** as a keyless local agent (model + Base URL, no API-key field) and a Whisper
  **Device** selector (cpu / auto / cuda / vulkan / coreml) for GPU-accelerated STT.
- **Voxa Studio: dictation core (VST-004, foundation).** A new headless `DictationSession` service —
  push-to-talk capture of the mic into an utterance buffer, then local whisper.cpp transcription —
  walking `Idle → Recording → Transcribing → Completed/Failed` for the UI to render. Avalonia-free and
  unit-tested with fakes. The dictation *view* + global push-to-talk hotkey + floating session pill,
  and the Config grey-out-incompatible selectors / Metrics provenance polish, are the remaining
  interactive UI work.
- **Local voice cloning for the sidecar TTS provider (VVL-002).** `Voxa:Tts: "Sidecar"` now exposes
  `IVoiceCloneProvider` via `ResolveCloner`: cloning persists a reference clip and returns its path as
  the voice, which the engine passes to the sidecar as the speaker reference (zero-shot — XTTS-v2 /
  OpenVoice). Keyless and fully local, with the consent gate in the host; fills VVL-001's deferred
  local-cloning slot, so Studio's clone wizard can target the local engine. Clips live under
  `Voxa:Sidecar:VoicesPath` (default: a subdirectory of the model cache).
- **Expressive / cloning TTS via an out-of-process sidecar (VVL-002, foundation).** A new
  `Voxa.Speech.Sidecar` package runs heavy PyTorch voices (XTTS-v2 / OpenVoice) in a separate process
  — the same isolation Piper uses for espeak-ng — exposed as an ordinary `ITextToSpeechEngine` over a
  tiny stdio protocol (`SidecarProtocol`, unit-tested over an in-memory stream). Opt-in heavy tier:
  `AddProvider(SidecarDescriptors.Tts)` with `Voxa:Sidecar:ExecutablePath` (a built/frozen binary) or
  `Voxa:Sidecar:PythonScript` (dev). Ships the runnable Python sidecar source; the pinned per-platform
  frozen binaries + cache catalog, the XTTS-v2-vs-OpenVoice spike, and local cloning (same transport)
  are the documented next steps — no binary is bundled or SHA-pinned yet. See the package README.
- **Local LLM brain: first-class Ollama agent provider (VLS-003).** Set `Voxa:Agent:Provider` to
  `Ollama` for a fully-local, keyless conversation loop (Whisper STT → Ollama → Piper/Kokoro). It
  reuses the OpenAI-compatible client pointed at the local daemon (`Voxa:Agent:BaseUrl`, default
  `http://localhost:11434/v1`); `Voxa:Agent:Model` names the pulled model (default `llama3.2`). No API
  key and no new dependency; validation checks the endpoint shape without probing the daemon, so boot
  never depends on `ollama serve` already running.
- **`voxa` command-line interface (VDX-003).** A new `Voxa.Cli` dotnet tool — Core's headless entry
  point (the CLI half of "Core = SDK + CLI"). `voxa transcribe <wav>` (whisper.cpp STT to stdout),
  `voxa say "<text>" [--out f.wav]` (Piper/Kokoro TTS to a WAV), `voxa models [list | purge]` (inspect
  or clear the SHA-256-pinned model cache), and `voxa check <appsettings.json>` (validate a pipeline
  config — providers, models, credentials — without downloading). Install with
  `dotnet tool install -g Voxa.Cli`.
- **MCP server: give your agent a voice and ears (VDX-002).** A new `Voxa.Mcp` dotnet tool runs a
  Model Context Protocol server over stdio (built on the official `ModelContextProtocol` SDK),
  exposing `voxa_speak` (text → WAV via Piper/Kokoro), `voxa_transcribe` (WAV → text via whisper.cpp)
  and `voxa_list_voices` — all backed by the keyless local tier, so any MCP-aware agent (Claude Code,
  Cursor, …) gets a voice you own with no API key. Install with `dotnet tool install -g Voxa.Mcp` and
  register the `voxa-mcp` command as an MCP server.
- **Local STT: bigger Whisper models + opt-in GPU (VLS-002).** The `WhisperCpp` catalog gains the
  `medium`, `large-v3` and `large-v3-turbo` families (each with a `-q5_0` quantization), SHA-256-pinned
  like the rest. A new `Voxa:WhisperCpp:Device` key (`cpu` default, plus `auto` / `cuda` / `vulkan` /
  `coreml`) selects the Whisper.net native runtime; the GPU natives are **opt-in** — add the matching
  `Whisper.net.Runtime.*` package to your app (Voxa never bundles them, so the default package is
  unchanged and CPU-deterministic). Explicit GPU backends fail loudly at startup if their runtime can't
  load, and a large/medium model on CPU logs a real-time-latency warning. Guide
  [`docs/local-speech.md`](docs/local-speech.md); spec
  [`docs/specifications/vls-002-gpu-stt-catalog-spec.html`](docs/specifications/vls-002-gpu-stt-catalog-spec.html).
- **Voxa Studio: Settings dialog with persistent provider credentials (VST-003).** A new **Settings**
  dialog (the gear at the foot of the nav rail) manages which providers are active and stores their
  API keys. Add a provider from a card-grid picker (OpenAI, Azure, ElevenLabs, Mistral), enter its key
  once, and it is encrypted to disk via **Windows DPAPI** (`~/voxa-secrets.dpapi`, scoped to your user
  account) and live from the next launch — no environment variables, no re-typing. Identities are
  modelled by *role*: one OpenAI key powers Whisper STT, OpenAI TTS *and* the chat agent at once;
  Mistral covers STT + TTS. Local providers (Whisper/Piper/Kokoro/Echo) are always listed and need no
  keys. Config's STT/TTS/Agent dropdowns now filter to **activated-or-local** providers, so a fresh
  Studio offers just the keyless local tier until you add a cloud provider. Secrets are a dedicated
  configuration layer, so a Config **Apply** never wipes stored keys, and they are never written into
  any export. New guide [`docs/settings.md`](docs/settings.md); spec
  [`docs/specifications/vst-003-settings-dialog-spec.html`](docs/specifications/vst-003-settings-dialog-spec.html).
- **Voxa Studio: selectable themes (VST-003).** Settings gains an **Appearance** category with a live
  theme picker — **Warm** (default), **Cool** (the original cyan brand), **Slate**. Switching repaints
  the whole app instantly (brushes are mutated in place; the brand mark and Talk bubbles follow the
  accent) and the choice persists to `~/voxa-studio-prefs.json`. The five pipeline stage colours stay
  fixed across themes — they encode meaning in the waterfall, traces and charts.
- **Voxa Studio: the launch splash now lingers long enough to see (VST-003).** On a fast machine the
  local boot finished before the ~2.2 s brand intro played; the splash now stays up for a minimum so
  the mark and wordmark animate. Skipped under reduced-motion and bypassed by a click.
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

### Changed

- **Voxa Studio: redesigned toward a calmer, Claude-like look (VST-003).** Warm-neutral surfaces and a
  single coral accent replace the cool ink + cyan palette; buttons, icons, radii and type are more
  compact. The Settings dialog is a borderless modal with a category sidebar (Providers · Appearance),
  each category shown on its own — providers are invisible under Appearance and vice-versa.
- **Voxa Studio: the Builder palette is drag-to-add.** Dragging a node from the palette onto the canvas
  is now the only way to add one (it lands where you drop it); clicking a palette item no longer places
  a node. The dangling-port **+** quick-add is unchanged.
- **Voxa Studio: removed decorative colour dots.** The Builder's per-node stage-colour bar, palette
  dots and inspector dot are gone (the kind label is now neutral). Dots that signal *state* — provider
  status, the live-session dot, validity, cached, listening/speaking — are kept.

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
