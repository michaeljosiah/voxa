# Speech-Core Parity Program — sequenced work-item plan

**Status:** proposed (scope-approval draft, not yet started) · **Author:** design pass, 2026-06-19 (rev: ONNX model tier — VLS-006/007/008)
**Origin:** competitive analysis of [`soniqo/speech-core`](https://github.com/soniqo/speech-core) (C++17 on-device voice-agent engine).
**Per-item HTML specs in `docs/specifications/` are authored alongside this plan — numbered for ordered implementation. No code lands before its spec.**

## Framing

speech-core is an **on-device, native (C++17) voice-agent engine** — the opposite end of the design space from Voxa
(a .NET, cloud-first pipeline with a local tier). They are not direct competitors, but speech-core has explored
**real-time turn-taking mechanics, full-duplex audio, and on-device model breadth** further than Voxa has, and several
of those ideas port cleanly onto Voxa's frame/processor model. This program captures exactly the transferable subset,
sequenced by value-per-effort and dependency, and slotted onto the existing ROADMAP buckets (P0 Latency, P1/P2
Echo & barge-in, P6 Capability).

Two themes run through it:

1. **Measure, then improve.** Voxa has latency micro-benchmarks (`bench/Voxa.Benchmarks`) and a per-turn latency
   waterfall (`VoxaDiagnosticsHub` / `StageLatencyTracker`), but **no behavioural turn-taking quality benchmark**. You
   cannot tell whether smart-turn, eager STT, or barge-in changes actually *improve conversation* without one. So the
   program opens with the benchmark (VRT-001) and every turn-taking item is gated against it.
2. **Half-duplex → full-duplex.** Voxa today gates the mic while the bot speaks. speech-core's contribution here is a
   set of **named seams and tunable parameters** (`EchoCancellerInterface`, eager STT, deferred-interruption,
   interruption-recovery, empty-STT recovery) that move a cascade toward true barge-in without committing to a specific
   DSP or model. We import the seams and the robustness checklist, not the C++.

## Product boundary — Voxa (Core) vs Voxa Studio

This program respects the same line the Voicebox-Parity program drew, and keeps the project predominantly-.NET:

- **Voxa (Core)** is the real-time voice **pipeline**, consumed headlessly as an **SDK** (NuGet) + **CLI**. Everything
  here is a *processor*, an *engine/seam*, or a *benchmark harness*; nothing is interactive. All turn-taking, full-duplex,
  streaming, enhancement, and diarization work lives here.
- **Voxa Studio** is the desktop GUI that **hosts** the Core pipeline. Its only role in this program is to *surface*
  Core's new signals — interim captions (VRT-004), a VAD/turn trace it already renders, diarized speaker labels
  (VLS-005), the benchmark's latency waterfall — never to own logic.

Each item below is tagged **[Core]**, **[Core + Studio]**, or **[Bench]**.

## What already exists (do not reinvent)

A precise read of the current tree — every item below is load-bearing for at least one spec:

| Capability | State in Voxa | Implication for this program |
|---|---|---|
| **Streaming STT contract** | **Already streaming-shaped.** `ISpeechToTextEngine` is `StartAsync` / `WriteAudioAsync` / `ReadTranscriptsAsync` (an `IAsyncEnumerable<TranscriptionResult>` yielding **interim + final**) / `StopAsync` / `FlushAsync`. `TranscriptionFrame` already carries `IsFinal` **and** `SpeakerId`. | VRT-004 is **not** a new seam — it is a real *streaming engine* + interim-frame *propagation*. VLS-005 fills the existing `SpeakerId` field; it does not add it. |
| **Smart-turn seam** | **Shipped (PR #26).** `SileroVadOptions.ConfirmTurnEnd(ReadOnlyMemory<byte>, ct) → bool` re-decides the turn at the silence timeout; `Voxa.Audio.SmartTurn` ships `HttpSmartTurnClassifier`. | Eager STT (VRT-002) is **complementary**, not a replacement: smart-turn decides *whether* the turn ended; eager decides *how fast to act once you believe it did*. The two compose. |
| **Pre-roll / speech-pad** | **Shipped.** `SileroVadOptions.PrerollDuration` (300 ms) replays pre-onset audio so the first syllable isn't clipped. | Do **not** re-add pre-roll as a "gap". |
| **Composer chain** | `DefaultVoicePipelineComposer.Compose(IServiceProvider)` builds `VAD → STT → TranscriptionFilter → agent → SentenceAggregator → TTS`, with HasListeners-guarded diagnostics taps inserted only when enabled. | This is the **single insertion point** for the AEC (VRT-003) and enhancement (VLS-004) stages: `mic → [AEC] → [enhance] → VAD → …`. |
| **VAD/provider descriptors** | `VoxaProviderDescriptor` + a per-family pattern (`Voxa*Descriptors.cs`); VAD resolves via `VoxaProviderRegistry.TryGetVad` → `CreateProcessor(sp, VoxaVadSettings)`. | New audio processors (AEC, enhancer) register the same self-describing way; the registry grows a `TryGetEnhancer` / `TryGetAec` in the same shape. |
| **Interruption mechanism** | Per-frame `CancellationToken` (reused, replaced only after an interruption); `InterruptionFrame`; `IUninterruptible` (tool calls, `EndFrame`). `WebSocketAudioSink` already **purges queued bot audio** on an interruption epoch (VPS-001). | Eager/speculative STT cancellation and the interruption-debounce (VRT-002) build on this exact machinery — no new cancellation model. |
| **Latency measurement** | `VoxaDiagnosticsHub` (zero-cost when unobserved), `StageLatencyTracker`, the `voxa.turn.ttfb` metric, Studio's Metrics view. | VRT-001 reuses these as its timing substrate rather than inventing new instrumentation. |
| **Cloud full-duplex** | `OpenAIRealtimeProcessor` + `AzureVoiceLiveProcessor` — composite STT+LLM+TTS+VAD with server-side VAD and native interruption. | VRT-005 mirrors this **composite** shape for a *local* speech-to-speech model; the seam is a third member of the same family. |
| **Out-of-process / pinned-artifact pattern** | `PiperProcessHost` (pooled child process, GPL isolation); `VoxaModelArtifact(Id, Url, Sha256, Size)` + `VoxaModelCache` (resolve/verify/extract, offline mode, `VOXA_MODEL_CACHE`). | VLS-004's ONNX model and any native AEC/S2S binary reuse this verbatim. |
| **On-device voice cloning** | Already an item — **VVL-002** (Voicebox-Parity program): local cloning via the frozen Python sidecar. | **Out of scope here** — do not duplicate. speech-core's VoxCPM2 confirms the use case; VVL-002 owns the delivery. |
| **GPU acceleration** | **VLS-002** adds an opt-in Whisper GPU seam. | Referenced as the accelerator path for heavy models (enhancement, S2S); not re-specced. |
| **In-process ONNX models** | `SileroVadEngine` + `KokoroTtsEngine` each create and manage their **own** ONNX-Runtime `InferenceSession` directly — there is no shared host and no uniform device/EP selection across them. | **VLS-006** generalizes this into one ONNX model host (the `OnnxEngine` analogue from speech-core), so new HuggingFace ONNX models plug in uniformly and inherit VLS-002's device seam. |

## The architectural through-line: seams over engines

speech-core gets its breadth from a **pure-orchestration core + opt-in model backends behind abstract interfaces**
(`STTInterface`, `EchoCancellerInterface`, `DiarizerInterface`, `FullDuplexSpeechInterface`). Voxa already follows the
same discipline (`Voxa.Core` zero-dep; engines behind `ISpeechToTextEngine` / `ITextToSpeechEngine`; VAD behind a
descriptor). So the dominant move in this program is **"define the seam, ship a default, defer the heavy model"**:

- VRT-003 ships the `IEchoCanceller` *interface* + a null/passthrough default + the chain wiring — not a DSP.
- VRT-005 ships the `ISpeechToSpeechSession` *interface* + a composite processor — the local model is deferred.
- VLS-005 ships the diarization *seams* + the pure-C# clustering orchestration — the ONNX reference impls are an
  opt-in package, mirroring how `DiarizationPipeline` is core-pure in speech-core.

This keeps every cross-cutting invariant intact (zero-dep Core, opt-in tiers, offline default lane) while positioning
Voxa for the heavy models as they mature or as demand appears.

---

## ONNX model coverage (HuggingFace)

speech-core's model breadth comes from a **shared ONNX Runtime host** (`OnnxEngine`) plus thin per-model wrappers. Voxa
runs ONNX in-process today only in one-offs (`SileroVadEngine`, `KokoroTtsEngine`), each managing its own session — so
pulling in a new HuggingFace ONNX model means re-deriving session / device / cache plumbing every time. **VLS-006 fixes
that** with one host; the model families below then plug in as thin engines. Every model is SHA-256-pinned in a catalog
and resolved through `VoxaModelCache` (the vls-002 hash rule applies — **never fabricate a hash; never ship a model whose
licence isn't cleared**, mirroring the Kokoro/GPL license-closure gate).

The six requested HuggingFace models, and where each lands:

| Model (HuggingFace ONNX) | Task | Home spec | Status / notes |
|---|---|---|---|
| **Parakeet TDT v3 (0.6B)** | Batch STT (high accuracy) | **VLS-007** on the **VLS-006** host | new local ASR engine; FastConformer encoder + TDT decoder; mel front-end |
| **Nemotron Speech Streaming (0.6B)** | Streaming STT (partials) | **VLS-007** (+ **VRT-004** interim plumbing) | the *local* streaming engine VRT-004 deferred; ~RTF 1.0 on CPU |
| **Nemotron-3.5 ASR Streaming Multilingual (0.6B)** | Streaming multilingual STT | **VLS-007** (+ **VRT-004**) | prompt-conditioned; multilingual; ONNX-FP16 |
| **DeepFilterNet3** | Speech enhancement / denoise (live) | **VLS-004** (seam + processor) running on the **VLS-006** host | already specced; the *model* moves onto the shared host |
| **Sidon** | Speech restoration (offline, 16→48 kHz) | **VLS-008** on the **VLS-006** host | denoise + dereverb whole-clip; consumer = clone-reference cleanup (**VVL-002** / Studio) |
| **PersonaPlex 7B** | Full-duplex speech-to-speech (CUDA) | **VRT-005** (seam) + **VLS-006**/**VLS-002** (host + GPU) | seam ships now; the GB-scale, GPU-only model is deferred / spike-gated |

So of the six: **two already have homes** (DeepFilterNet3 → VLS-004, PersonaPlex → VRT-005), and the remaining four are
covered by the three new ONNX-tier items below (**VLS-006** host, **VLS-007** ASR, **VLS-008** Sidon). DeepFilterNet3 and
the VLS-005 diarization models (Pyannote, WeSpeaker) are existing ONNX consumers that **should adopt the VLS-006 host**
once it lands — an additive refactor, not a prerequisite.

---

## Work items

> IDs are **provisional** but numbered for ordered implementation. New prefix **VRT** = *Voxa Real-Time* (turn-taking &
> full-duplex); model-breadth items slot onto the existing **VLS** (local speech); the FFI item onto **VDX** (developer
> experience / headless surfaces). Each item is a `docs/specifications/<id>-*-spec.html` document and a ROADMAP entry.
> Every item obeys the cross-cutting invariants at the bottom.

### VRT-001 · **[Bench]** — Turn-taking quality benchmark (Full-Duplex-Bench harness)  *(measurement foundation — do first)*
- **Goal:** make "does this improve turn-taking?" an answerable, regression-gated number — the prerequisite for every
  other VRT item.
- **Why speech-core:** it integrates [Full-Duplex-Bench v1.0](https://github.com/DanielLin94144/Full-Duplex-Bench)
  (arXiv 2503.04721, ASRU 2025) end-to-end — driver → per-sample JSON → summary CSV → category scoring → checked-in
  baseline → weekly CI gate. Voxa is a cascade, so the same three cascade-fair categories apply.
- **Scope (in):**
  - A new `bench/Voxa.TurnTaking` harness (console + library) that walks the FDB v1.0 corpus layout, runs each sample
    through the **composed pipeline** (`DefaultVoicePipelineComposer.Compose`) with mock or local engines, and writes
    `<category>__<id>.json` (timings: stt/llm/tts/ttft/total_wall) + the response WAV.
  - A summary roll-up (per-category p50/p90/p99) and a scorer for the three **cascade-fair** categories: **pause
    handling**, **smooth turn-taking**, **user interruption** (backchannel is architecturally N/A for a cascade — log
    it as skipped, don't fake it).
  - A checked-in `baseline.json` + a default-lane smoke fixture (a handful of samples, like speech-core's `fdb_mini`)
    and a `Category=LocalModels`-gated full run.
- **Scope (out):** the backchannel category; full corpus in CI (gate on a mini fixture); an LLM-judge (opt-in later).
- **Key files:** new `bench/Voxa.TurnTaking/`; reuses `DefaultVoicePipelineComposer`, `VoxaDiagnosticsHub` /
  `StageLatencyTracker` (timings), `Voxa.Testing` WAV fixtures.
- **Risks:** corpus hosting (Drive, no single tarball) → ship a tiny checked-in fixture, document the full fetch;
  determinism (mock engines for the hermetic lane; real engines trait-gated).
- **Tests:** the smoke run is itself a `ctest`-equivalent xUnit test against the fixture; baseline-diff gate.
- **Effort:** ~1 week.

### VRT-002 · **[Core]** — Eager STT & turn-taking robustness  *(highest near-term latency/robustness win)*
- **Goal:** cut perceived turn latency and close a checklist of barge-in/turn edge cases, all measured by VRT-001.
- **Why speech-core:** its `AgentConfig` codifies hard-won mechanics Voxa lacks — eager STT (~0.3 s saved), deferred
  interruption, interruption-recovery, empty/low-confidence STT recovery, force-split, response-duration cap.
- **Scope (in):**
  - **WS1 — Eager/speculative STT.** When silence reaches a new `EagerSttDelay` (< `StopDuration`), speculatively
    trigger STT (and optionally LLM prefetch) on the buffered utterance *before* `StopDuration` / `ConfirmTurnEnd`
    confirms end-of-turn; **discard on resume** (user speaks again within the window) via the existing per-frame
    `CancellationToken`. Mark the speculative utterance so resumed speech isn't mistaken for an interruption. Composes
    with the smart-turn seam.
  - **WS2 — Robustness checklist** (each individually testable, each a tunable knob):
    - *Interruption debounce* — require `MinInterruptionDuration` of confirmed user speech during bot playback before
      emitting `InterruptionFrame` (filters echo residual / coughs).
    - *Interruption recovery* — if the user stops within `InterruptionRecoveryTimeout`, resume playback instead of
      reprocessing.
    - *Empty / low-confidence STT recovery* — on empty or sub-threshold STT, reset turn state + bot-speaking flags so
      the pipeline can't wedge (speech-core calls this out explicitly as a stuck-state fix).
    - *Force-split* — cap utterance duration (`MaxUtteranceDuration`); force-emit `UserStoppedSpeakingFrame` to bound
      memory and trigger intermediate transcription.
    - *Response-duration cap* — bound TTS output (`MaxResponseDuration`) against runaway/hallucinated synthesis.
- **Scope (out):** the smart-turn classifier itself (shipped, PR #26); AEC (VRT-003).
- **Key files:** `SileroVadProcessor` / `SileroVadOptions` (eager dispatch, force-split, debounce), the
  `AgentLoopProcessor` / `MicrosoftAgents` turn worker (speculative kickoff, empty-STT recovery, response cap),
  `WebSocketAudioSink` (recovery interplay with the existing interruption purge), tuning profiles.
- **Risks:** eager + smart-turn interaction (define precedence: smart-turn `false` overrides an eager dispatch);
  speculative work wasted on resume (bounded; measure net win via VRT-001).
- **Tests:** headless VAD-sequence tests for each knob (synthetic probability streams, like the existing VAD tests);
  a VRT-001 before/after delta.
- **Effort:** ~1–1.5 weeks.

### VRT-003 · **[Core]** — Acoustic echo cancellation seam (`IEchoCanceller`)  *(full-duplex foundation)*
- **Goal:** give Voxa a first-class seam for true barge-in (user interrupts over speakers) without committing to a DSP.
- **Why speech-core:** `EchoCancellerInterface` + the auto far-end feed (`mic → AEC → enhance → VAD → STT`) is exactly
  the seam Voxa is missing; today Voxa is half-duplex (mic gated during playback, ROADMAP P1).
- **Scope (in):**
  - An `IEchoCanceller` abstraction (in `Voxa.Speech.Abstractions` or a new `Voxa.Audio.Abstractions`): `FeedReference`
    (TTS far-end), `CancelEcho` (near-end mic), `Reset`, sample-rate contract — mirroring speech-core's interface.
  - An `EchoCancellerProcessor` placed **before** the VAD in the composer, auto-fed the TTS output as far-end reference
    (wired from the TTS stage / bot-audio path).
  - A **null/passthrough default** (byte-identical to today) + a descriptor/registry slot (`TryGetAec`) so a real
    implementation (WebRTC APM, SpeexDSP via P/Invoke, or a future managed AEC) drops in by config.
- **Scope (out):** shipping a production AEC DSP (separate item once a license-clean .NET binding is chosen); shipping
  the *barge-in policy* (that's VRT-002's debounce/recovery — AEC just makes it robust on speakers).
- **Key files:** new abstraction + `EchoCancellerProcessor`; `DefaultVoicePipelineComposer` (insertion + reference
  feed); `VoxaProviderRegistry` (`TryGetAec`); the bot-audio/far-end plumbing.
- **Risks:** frame alignment + sample-rate conversion between far-end (TTS rate) and near-end (mic rate) — push into
  the implementation, keep the seam simple (speech-core does exactly this); reference-feed thread-safety on the audio
  hot path.
- **Tests:** passthrough golden (off = byte-identical); a fake AEC verifies `FeedReference`/`CancelEcho` ordering and
  that far-end is fed during synthesis.
- **Effort:** ~1 week (seam + wiring + null default); a real DSP binding is a follow-up.

### VRT-004 · **[Core + Studio]** — Streaming STT: interim transcriptions end-to-end + first streaming engine
- **Goal:** realise the already-streaming STT contract — lower local-tier latency, live partial captions — complementing
  ROADMAP P0 (Deepgram / Azure streaming).
- **Why speech-core:** its Nemotron streaming STT shows on-device partials at ~RTF 1.0. Voxa's contract is already
  shaped for this — and interims **already propagate today** (`AzureSpeechToTextEngine` emits `IsFinal:false`,
  `SpeechToTextProcessor.ReadLoopAsync` forwards every result ungated, `AgentLoopProcessor` matches `{IsFinal:true}`
  only). The gap is that Studio doesn't render the live caption, nothing rate-bounds interim churn, and the batch/local
  tier produces no interims at all.
- **Scope (in):**
  - **Render + rate-bound the interims that already flow**: a Studio live caption from the existing diagnostics
    `TranscriptEvent`; an additive `InterimMinInterval` coalescing knob on `SpeechToTextProcessor` so partial churn
    can't flood the bounded channel; a regression test that the agent (already finals-only) keeps ignoring interims —
    **no suppression gate; interims keep flowing**.
  - **A lower-latency / local streaming engine**: Azure already runs continuous recognition and already emits interims
    (the proof case), so the genuine engine gaps are *latency* (a new `Voxa.Speech.Deepgram`, sub-200 ms) and the
    *local* streaming tier (Nemotron via VLS-007).
  - Optionally feed the latest interim text to the smart-turn classifier (richer signal than audio alone).
- **Scope (out):** a *local* streaming STT model lands via VLS-007; Cartesia/streaming TTS (separate P0 TTS bullet).
- **Key files:** `SpeechToTextProcessor` (rate-coalesce interims), the agent finals-only regression test, the
  diagnostics/Studio caption surface, `Voxa.Speech.Azure` (mapping test) / a future `Voxa.Speech.Deepgram`.
- **Risks:** interim churn flooding the pipeline (throttle / coalesce); making sure interims never trigger a turn or
  feed the agent.
- **Tests:** processor emits interim then final from a fake streaming engine; filter/agent ignore interims; engine
  contract test behind `Category=LocalModels` or a mocked socket.
- **Effort:** ~1–1.5 weeks.

### VLS-004 · **[Core]** — Local speech enhancement / denoise processor (`IAudioEnhancer`)
- **Goal:** improve local STT accuracy in noisy/reverberant conditions — the edge/offline scenario Voxa already invests
  in.
- **Why speech-core:** DeepFilterNet3 (denoise) + Sidon (restore) are ONNX models wired as `EnhancerInterface`; Voxa
  has only an RMS floor, no spectral enhancement.
- **Scope (in):**
  - An `IAudioEnhancer` seam (`Enhance(pcm) → pcm`, sample-rate contract) + an `AudioEnhancerProcessor` placed
    **before** the VAD (`mic → [AEC] → [enhance] → VAD → STT`).
  - One reference ONNX impl (DeepFilterNet3 — **licence pending verification**; if its weight-redistribution terms
    don't clear, substitute a permissively-licensed RNNoise/NSNet2 export, per VLS-004) in a new `Voxa.Audio.Enhance`
    package, model SHA-256-pinned in a catalog via `VoxaModelCache`; **opt-in**, default chain unchanged.
- **Scope (out):** Sidon-style offline restoration of clone references (belongs with VVL-002 / Studio artifacts);
  GPU enhancement (rides VLS-002 patterns if needed).
- **Key files:** new `Voxa.Audio.Enhance` package + descriptor; `DefaultVoicePipelineComposer` insertion;
  `VoxaModelCache` catalog entry; profile/config key.
- **Risks:** added per-frame latency (measure with the diagnostics waterfall; keep opt-in); ONNX-RT in-process
  coexistence with Kokoro/Silero (already present — confirm no EP conflict); license closure on the model weights.
- **Tests:** passthrough-off golden; enhancement-on round-trip (shape/rate preserved) behind `Category=LocalModels`;
  catalog resolve/offline-miss in the default lane.
- **Effort:** ~1 week.

### VLS-005 · **[Core]** — Speaker diarization (`Voxa.Audio.Diarization`)
- **Goal:** add an entire missing capability — speaker-labelled (multi-speaker / meeting) transcription — filling the
  already-present but always-null `TranscriptionFrame.SpeakerId`.
- **Why speech-core:** `DiarizationPipeline` composes `SegmentationInterface` + `EmbeddingInterface` + agglomerative
  clustering as **pure C++ with no ML-runtime dep in the orchestration** — a design that fits `Voxa.Core`'s zero-dep
  discipline exactly.
- **Scope (in):**
  - A new `Voxa.Audio.Diarization` package with seams `ISpeakerSegmentation`, `ISpeakerEmbedding`, `IDiarizer`, and a
    **pure-C# `DiarizationPipeline`** (constrained agglomerative clustering — no ML runtime of its own), batch/offline.
  - Reference ONNX impls (Pyannote segmentation + WeSpeaker embedding) in an opt-in sub-package, models pinned via
    `VoxaModelCache`.
  - Wire diarized labels into `TranscriptionFrame.SpeakerId` for the batch/transcription path (a `voxa transcribe
    --diarize` CLI verb is the natural first consumer; live diarization is out of scope).
- **Scope (out):** real-time/streaming diarization; speaker *identification* (enrollment against known voices) — a
  follow-up once embeddings exist.
- **Key files:** new `Voxa.Audio.Diarization` package (core seams + clustering) + an `*.Onnx` impl sub-package; CLI
  verb in `Voxa.Cli`; pinned catalog.
- **Risks:** clustering-threshold tuning (expose via `DiarizerConfig`, default to speech-core's 0.715); large-ish
  models (opt-in tier); keeping the orchestration ML-runtime-free.
- **Tests:** clustering unit tests on synthetic embeddings (deterministic, default lane); a real two-speaker WAV
  behind `Category=LocalModels`.
- **Effort:** ~1.5–2 weeks.

### VLS-006 · **[Core]** — Shared ONNX Runtime model host  *(foundation for the ONNX model tier)*
- **Goal:** one reusable, device-aware ONNX-Runtime host so any HuggingFace ONNX model plugs into Voxa with uniform
  session management, execution-provider/device selection, and pinned-catalog resolution — the prerequisite for
  VLS-007/008 and the answer to "how do we keep adding ONNX models cheaply".
- **Why speech-core:** its `OnnxEngine` (an ORT singleton with NNAPI/QNN/CPU EP selection + a `SessionOptionsHook`) is
  shared by every ONNX model; Voxa instead re-implements session setup per engine.
- **Scope (in):**
  - A new `Voxa.Audio.Onnx` package: an ONNX-Runtime session host with cached sessions, an EP/device seam (CPU default;
    CUDA / DirectML / CoreML opt-in, reusing VLS-002's `Device` pattern and its "GPU natives are user-added, never
    bundled" rule), a `SessionOptions` hook for custom providers, and tensor-buffer helpers.
  - `VoxaModelCache` integration so a model is one pinned `VoxaModelArtifact` (+ any sidecar files: vocab / tokenizer /
    config) resolved/verified/extracted the standard way.
  - A descriptor/registry shape so an ONNX model self-describes (id, files, EP support) like the existing provider descriptors.
- **Scope (out):** rewriting `SileroVadEngine` / `KokoroTtsEngine` onto the host (an additive *follow-up* refactor, not a
  prerequisite); any specific model (those are VLS-004/005/007/008).
- **Key files:** new `Voxa.Audio.Onnx` package; mirror the `KokoroTtsEngine` / `SileroVadEngine` session patterns; reuse
  `VoxaModelArtifact` / `VoxaModelCache` and VLS-002's device seam.
- **Risks:** ORT CPU vs GPU package coexistence/conflict in-process (the exact issue VLS-002 flagged for Kokoro) — keep
  GPU opt-in, document the constraint; one ORT version pinned across all models.
- **Tests:** host loads a tiny pinned ONNX model + runs one inference (default lane if small/MIT, else `Category=LocalModels`);
  device-string parse/validate offline.
- **Effort:** ~1 week.

### VLS-007 · **[Core]** — Local ONNX ASR engines (Parakeet TDT v3 + Nemotron streaming + Nemotron-3.5 multilingual)
- **Goal:** add a high-accuracy, local, ONNX-Runtime STT tier alongside whisper.cpp — including the *streaming* engines
  that finally give the offline tier partials (closing the gap whisper.cpp's batch path leaves).
- **Why speech-core:** it ships exactly these as ONNX `STTInterface` impls — Parakeet TDT v3 (batch), Nemotron streaming
  (begin/push_chunk/end), Nemotron-3.5 multilingual (prompt-conditioned).
- **Scope (in):**
  - A new `Voxa.Speech.OnnxAsr` package implementing `ISpeechToTextEngine` on the VLS-006 host, three model families,
    each pinned in a catalog (model + tokenizer/vocab + config):
    - **Parakeet TDT v3 (0.6B)** — batch (`transcribe` on `UserStoppedSpeaking`/`FlushAsync`), FastConformer + TDT, mel front-end.
    - **Nemotron Speech Streaming (0.6B)** — true streaming: the begin/push-chunk/end loop maps onto `WriteAudioAsync` →
      interim `TranscriptionResult(IsFinal:false)`, finalised on flush. **Consumes VRT-004's interim propagation.**
    - **Nemotron-3.5 ASR Streaming Multilingual (0.6B)** — streaming + multilingual + prompt-conditioned (a language/context hint).
  - A descriptor + config (`Voxa:Stt = OnnxAsr`, model name, device) matching the WhisperCpp descriptor shape; device via VLS-006/VLS-002.
- **Scope (out):** non-ONNX (LiteRT) backends; on-device fine-tuning. The audio pre/post-processing (mel, tokenizer,
  TDT/RNNT decode) is the hard part — port carefully, validating against speech-core's reference outputs.
- **Key files:** new `Voxa.Speech.OnnxAsr` package + descriptor; the VLS-006 host; `ISpeechToTextEngine` (already
  streaming-shaped); pinned catalogs; reuse the `SpeechToTextProcessor` interim path from VRT-004.
- **Risks:** **licence review** (NeMo/Parakeet weights — typically CC-BY-4.0; confirm + record, mirror the Kokoro
  license-closure gate); decoder correctness (TDT/RNNT is fiddly — test against known transcripts); model sizes (0.6B →
  opt-in tier, GPU-friendly via VLS-006).
- **Tests:** WER/round-trip on a known clip behind `Category=LocalModels`; the streaming engine emits interim-then-final
  against the VRT-004 processor path; catalog resolve/offline-miss in the default lane.
- **Effort:** ~2–3 weeks (the pre/post-processing port is the long pole; batch Parakeet first, then the streaming pair).

### VLS-008 · **[Core + Studio]** — ONNX speech restoration (Sidon)  *(offline; clone-reference cleanup)*
- **Goal:** an offline denoise + dereverb restorer — primarily to clean a reverberant voice-cloning reference before TTS
  cloning, and as a general WAV-cleanup utility.
- **Why speech-core:** `OnnxSidonRestorer` (a w2v-BERT predictor + DAC vocoder, 16→48 kHz) + a `speech_sidon_restore` CLI;
  explicitly the "clean a clone reference" tool.
- **Scope (in):**
  - A Sidon restorer on the VLS-006 host (predictor + vocoder ONNX graphs + the SeamlessM4T-style log-mel front-end),
    pinned in a catalog; any-rate input → 48 kHz mono.
  - A `voxa restore <in.wav> <out.wav>` CLI verb (mirrors speech-core's CLI) as the first concrete consumer; **VVL-002 /
    Studio** wire it into the cloning wizard as a "clean reference" step later.
- **Scope (out):** real-time / live-pipeline restoration — it is whole-clip and offline, so it does **not** go in the
  `mic → … → VAD` chain (that's VLS-004's live denoiser); the Studio cloning-wizard UI hook (rides VVL-002).
- **Key files:** the VLS-006 host; a new restorer + pinned catalog; a `Voxa.Cli` `restore` verb; (later) a VVL-002/Studio consumer.
- **Risks:** two-graph pipeline + custom front-end (port carefully); licence on the Sidon weights + DAC vocoder (verify);
  large-ish models (opt-in).
- **Tests:** restore round-trip (any-rate in → 48 kHz mono out) behind `Category=LocalModels`; CLI arg-parse in the default lane.
- **Effort:** ~1 week (low priority — sequence after VVL-002 has a use for it).

### VRT-005 · **[Core]** — Local full-duplex speech-to-speech seam (`ISpeechToSpeechSession`)  *(strategic / exploratory)*
- **Goal:** position Voxa for the frontier — a local Moshi/PersonaPlex-class speech-to-speech model — with a seam in the
  same family as the existing cloud realtime composites.
- **Why speech-core:** `FullDuplexSpeechInterface` (respond-stream of audio+text, voice preset, system prompt, KV-cache
  session) is the template; Voxa already has the *composite* shape for OpenAI Realtime / Azure Voice Live.
- **Scope (in):**
  - An `ISpeechToSpeechSession` seam (user-audio-in → agent-audio+text-out streaming, `SetVoice`, `SetSystemPrompt`,
    `ResetSession`, `Cancel`) modelled on the existing realtime processors' surface.
  - A composite `SpeechToSpeechProcessor` that slots in where `OpenAIRealtimeProcessor` does (full-duplex, owns its own
    VAD/turn-taking), emitting the same lifecycle/diagnostics frames as the cloud composites.
- **Scope (out):** **the model itself** — local S2S weights (PersonaPlex-class) are GB-scale and often GPU-only; the
  seam ships, the model is deferred behind a quality/feasibility spike (and likely an out-of-process or GPU host,
  reusing VLS-002 / sidecar patterns). A cloud S2S could validate the seam first.
- **Key files:** new seam in Abstractions; `SpeechToSpeechProcessor` peer to `OpenAIRealtimeProcessor`; composer/registry
  wiring for a composite (bypasses the granular VAD→STT→…→TTS chain, like the cloud composites do).
- **Risks:** speculative until an open local S2S model is viable on commodity hardware; ensure the seam matches the
  cloud-composite contract so it's a true third member, not a parallel universe.
- **Tests:** seam + processor lifecycle against a fake session (no model); diagnostics-frame parity with the cloud
  composites.
- **Effort:** ~1 week for the seam + composite; the model is a separate, spike-gated effort.

### VDX-004 · **[Core]** — Native C ABI / FFI for non-.NET embedding  *(exploratory — gated on mobile/native being a goal)*
- **Goal:** let non-.NET hosts (C, Swift, Kotlin) embed the Voxa pipeline — the reach speech-core gets from its C ABI.
- **Why speech-core:** its vtable C ABI is why it embeds into Android (JNI) and Apple (Swift). Voxa is .NET-only; its
  only cross-language surfaces are the MCP server (VDX-002) and CLI (VDX-003).
- **Scope (in):**
  - A NativeAOT-compiled `Voxa.Native` shared library exposing a minimal **C ABI** via `[UnmanagedCallersOnly]`:
    create/start/push-audio/stop/destroy a pipeline, plus an event callback (transcripts, audio-out, errors) — mirroring
    speech-core's `sc_pipeline_*` surface so the conceptual contract is familiar.
  - A C header + a smoke consumer (a tiny C program) proving the round-trip.
- **Scope (out):** mobile bindings (Android AAR / Swift package) — a *consumer* of this ABI, separate repos like
  speech-core's siblings; bringing local models into the AOT image (they stay external/pinned).
- **Key files:** new `src/Voxa.Native` (NativeAOT `csproj`, `[UnmanagedCallersOnly]` exports, a hand-written or
  generated `.h`); a C smoke test in CI.
- **Risks:** **load-bearing feasibility question** — NativeAOT + the pipeline's dependency graph (Microsoft.Extensions.AI,
  provider SDKs) may not all be AOT-clean; likely starts with a *Core-only* surface (bring-your-own engine via callback
  vtables, exactly like speech-core) rather than the full meta-package. Marshalling audio buffers across the boundary.
- **Tests:** C smoke consumer in CI (create → push WAV → receive transcript via callback → destroy).
- **Effort:** unknown until a feasibility spike; treat as **exploratory**, sequence last, do **not** start before a
  mobile/native consumer is an actual goal.

---

## Sequence & milestones

Ordered by *measure-first*, then value/dependency. Two tracks: **A — real-time / turn-taking** (VRT) and **B — local
model breadth / ONNX tier** (VLS). They are largely parallelizable; the only cross-track dependency is VLS-007's streaming
engines needing VRT-004's interim plumbing. VRT-001 is a hard prerequisite for trusting any turn-taking change.

```
M1   VRT-001            Turn-taking benchmark (FDB) ......... [Bench]         measurement foundation — do first
M2   VRT-002 + VRT-003  eager STT + AEC seam ................ [Core]          core turn-taking value (measured by M1)
M3   VRT-004 + VLS-004  streaming STT + denoise ............. [Core(+Studio)] latency, live captions, local accuracy
M4   VLS-006            shared ONNX Runtime host ............ [Core]          ONNX-tier foundation (unlocks track B)
M5   VLS-007 + VLS-005  ONNX ASR + diarization .............. [Core]          VLS-007 needs VLS-006 + VRT-004; both adopt the host
M6   VLS-008            ONNX restoration (Sidon) ............ [Core+Studio]   offline; low priority; rides VVL-002
M7   VRT-005            local speech-to-speech seam ......... [Core]          strategic seam; PersonaPlex model deferred
M8   VDX-004            native C ABI / FFI .................. [Core]          exploratory; gated on mobile being a goal
```

- **M1 — Measure.** VRT-001. ~1 wk. Nothing downstream is trustworthy without it.
- **M2 — Turn-taking & full-duplex foundation.** VRT-002 + VRT-003. ~2–2.5 wk. The highest-value pair (the comparison's "pick three" = M1 + M2).
- **M3 — Chain latency & accuracy.** VRT-004 + VLS-004. ~2 wk. Independent of each other.
- **M4 — ONNX host.** VLS-006. ~1 wk. The foundation the rest of the ONNX tier (VLS-007/008, and retroactively VLS-004/005) plugs into.
- **M5 — ONNX ASR + diarization.** VLS-007 (~2–3 wk) + VLS-005 (~1.5–2 wk). VLS-007's streaming engines consume VRT-004; both adopt the VLS-006 host. Parallelizable.
- **M6 — Restoration.** VLS-008. ~1 wk. Offline; low priority; lands when VVL-002 has a use for it.
- **M7 — S2S seam.** VRT-005. ~1 wk for the seam; the PersonaPlex-class model is a separate spike (GPU/host).
- **M8 — C ABI.** VDX-004. Exploratory; only if mobile/native embedding becomes a goal.

Rough total: ~12–14 weeks for M1–M7 (M8 unbudgeted, exploratory), the two tracks parallelizable if staffed —
front-loaded with measurement (M1) and the highest-value turn-taking pair (M2).

## Cross-cutting invariants (every item)

- **`Voxa.Core` stays zero-dependency** (NUlid only). New seams go in `Voxa.Speech.Abstractions` or a new
  `Voxa.Audio.*` package; never add ASP.NET / Azure / ONNX / OpenTelemetry to Core.
- **Forward unhandled frames downstream** (especially `StartFrame` / `EndFrame`, or the sink never completes); accept
  the per-frame `CancellationToken` on any long-running work; mark guaranteed-completion frames `IUninterruptible`.
- **Backpressure convention** holds: audio/data paths use bounded `DropOldest` channels; control/system paths use
  `Wait`. New stages document any deviation.
- **Diagnostics stay zero-cost when unobserved** — every publish behind a `HasListeners` guard; with diagnostics off,
  the composed pipeline is **byte-identical** to today (golden-tested). New stages add taps the same way.
- **Config-capture rule:** providers that need configuration capture the `IConfiguration` passed to `AddVoxa`; never
  `GetRequiredService<IConfiguration>()` (breaks the plain-`ServiceCollection` hosts — Studio, tests, CLI).
- **Every model/binary is SHA-256-pinned** in a catalog (`VoxaModelArtifact`) and resolved through `VoxaModelCache`;
  bumping a pin is a code change with a test, never a floating reference.
- **Tests that download models carry `[Trait("Category", "LocalModels")]`**; the default lane stays offline and fast.
  GPL / heavy / native deps stay behind a process boundary with a license-closure gate test (mirror the Kokoro one).
- **Studio:** no Generic Host; view models Avalonia-free + headless-testable; `IStudioAudioDevice` seam; `voxa-design`
  tokens for any new surface.
- **Per PR:** one logical change, branch from `main`, an `[Unreleased]` CHANGELOG entry, warnings-as-errors clean, and
  the item's `docs/specifications/*.html` spec authored **before** its code.

## Open decisions (resolve at scope approval)

1. **VRT-004 first engine:** Azure continuous-recognition (reuses `Voxa.Speech.Azure`, no new package) **vs** a new
   `Voxa.Speech.Deepgram` (lower latency, new dependency). *Lean: Azure first (cheapest), Deepgram as a fast-follow.*
2. **VRT-003 AEC implementation target:** ship only the seam + null default now, **or** also bind one DSP (WebRTC APM
   has the cleanest cross-platform story but needs a license-clean .NET binding). *Lean: seam now, DSP as a follow-up.*
3. **VLS-005 packaging:** one `Voxa.Audio.Diarization` package (core seams + clustering) with the ONNX impls in a
   `.Onnx` sub-package, mirroring how speech-core keeps `DiarizationPipeline` core-pure. *Lean: yes, split.*
4. **VDX-004:** is non-.NET embedding actually a goal? If not, **do not start** — leave it specced as a placeholder.
5. **ONNX runtime sharing (VLS-006):** one shared ORT version + host for all ONNX models (Parakeet, Nemotron ×2,
   DeepFilterNet3, Sidon, Pyannote, WeSpeaker), CPU default with GPU opt-in per VLS-002's "natives are user-added" rule.
   *Lean: yes — one host, one pinned ORT version; GPU never bundled.*
6. **VLS-007 model priority:** all three ASR models at once, or batch Parakeet first then the streaming Nemotron pair?
   *Lean: Parakeet (batch, simplest decode path) first to prove the host + descriptor, then the streaming pair on VRT-004.*

_Each item becomes a session task and a ROADMAP entry once this sequence is approved._
