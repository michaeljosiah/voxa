# Voicebox-Parity Program — sequenced work-item plan

**Status:** proposed (scope-approval draft, not yet started) · **Author:** design pass, 2026-06-18
**Origin:** competitive analysis of [`jamiepine/voicebox`](https://github.com/jamiepine/voicebox).
**Per-item HTML specs in `docs/specifications/` follow once this sequence is approved — no code lands first.**

## Framing

Voicebox is a *local voice studio* (TTS generation, zero-shot cloning, dictation, an MCP "give-your-agent-a-voice"
server). It has **no VAD, no barge-in, batch STT, and local LLM only for transcript cleanup** — so its architecture
is not a model for Voxa's real-time pipeline. What's worth importing is its **local-model breadth, voice-I/O
ergonomics, and a few UI patterns**. This program captures exactly that, sequenced by value-per-effort and dependency,
and slotted onto the existing ROADMAP buckets (P6 Capability, P8 Studio).

## Product boundary — Voxa (Core) vs Voxa Studio

The whole program respects one line, and it keeps this a predominantly-.NET project:

- **Voxa (Core)** is the real-time voice **pipeline**, consumed headlessly as an **SDK** (NuGet) and a **CLI**. Its single
  job is to compose and mix-and-match providers / voices / agents into one optimal, highly-performant, low-latency
  real-time pipeline. Everything here is a *provider* or *processor*; nothing is interactive. New engines, agents, and
  VAD/turn logic live here.
- **Voxa Studio** is the desktop GUI. It **hosts the Core pipeline** and adds an interactive **voice-artifacts
  workbench** — generation, the cloning wizard, dictation, and (future) non-real-time manipulation: podcast
  time-stretch / speed-up, voiceover batch synthesis, effects — i.e. the rest of voicebox's territory. Anything that
  operates on voice *artifacts outside the live pipeline* belongs here, never in Core.
- **The sidecar rule:** the frozen Python sidecar registers as an ordinary Core `ITextToSpeechEngine` /
  `IVoiceCloneProvider`, so the SDK/CLI *can* use it in a pipeline — but its heavy, non-real-time, generation-oriented
  use is surfaced in **Studio**. Core's *recommended* real-time TTS stays Piper / Kokoro / cloud; the sidecar is the
  opt-in heavy tier whose natural home is the Studio workbench.

Each work item below is tagged **[Core]**, **[Studio]**, or **[Core + Studio]** against this line.

## What already exists (do not reinvent)

| Capability | State in Voxa | Implication for this program |
|---|---|---|
| Voice cloning seam | **Shipped, cloud-first (VVL-001):** `IVoiceCloneProvider` / `IVoiceCatalogProvider`, resolved via `VoxaTtsDescriptor.ResolveCloner`/`ResolveCatalog`; ElevenLabs + Mistral clone from samples; Studio Voices wizard is real and **consent-gated**. | The wizard is **not** a stub. Local cloning is the *explicitly deferred* slot ("OpenVoice tone-color converter pending a validated ONNX export + audio-quality spike"). Our item = implement a **local** `IVoiceCloneProvider`, not a new seam or UI. |
| TTS engine contract | `ITextToSpeechEngine.SynthesizeAsync(text, ct)` yields pooled PCM chunks. No prosody/instruct parameter. | Expressive control needs either inline tags (no contract change) or a small Abstractions-level options extension. |
| Provider registration | `VoxaSttDescriptor` / `VoxaTtsDescriptor` (Name, ConfigSection, Validate, CreateProcessor, optional `WarmUpAsync`). | New engines self-describe and register the same way; local engines get a `WarmUpAsync`. |
| Pinned artifacts | `VoxaModelArtifact(Id, DownloadUrl, Sha256, SizeBytes)` + `ArchiveEntry` + `Executable`; SHA-256-pinned catalogs; `VoxaModelCache` resolves/verifies/extracts; offline mode. | This is exactly the mechanism for pinning a **frozen Python sidecar binary** (see crux). |
| Out-of-process engine | Piper runs as a pooled child process (`PiperProcessHost`); espeak-ng (GPL) isolated behind a process boundary. | The sidecar reuses this proven pattern — not a new architecture. |
| Streaming STT/TTS, smart-turn | Planned in ROADMAP P0 (Deepgram, Cartesia, smart-turn via `SileroVadOptions.ConfirmTurnEnd`). | Out of scope here; this program is about model breadth + ergonomics, not latency. |
| GPU | **Absent** — CPU-only confirmed (no GPU/CUDA/CoreML references in `src/`). | Real gap → VLS-002. |
| Local LLM | Documented `IChatClient` DI path only; the sole keyless agent is `Echo`. | Real gap → VLS-003. |
| MCP "voice" server | None. | Genuinely new → VDX-002. |

## The architectural crux: how Voxa gets model breadth without becoming a Python app

Voicebox's 7 TTS engines are free *because it is Python* (`pip install` any Hugging Face model). Voxa is .NET; its two
local engines are exactly the ones with clean non-Python paths (Kokoro = ONNX, Piper = self-contained binary). The
expressive/cloning models we want (Chatterbox, XTTS, OpenVoice, Qwen3-TTS) are PyTorch.

**Reconciliation — treat the Python engine exactly like the Piper executable:** ship it as a per-platform,
**PyInstaller-frozen, SHA-256-pinned binary** registered as a `VoxaModelArtifact` (`Executable = true`), resolved by
`VoxaModelCache`, and driven out-of-process by the existing child-process host. This **preserves every core invariant**:

- no managed Python dependency and no `pip` at runtime (it's a frozen binary),
- SHA-256 pinning + offline/air-gap (it's just another pinned artifact in the cache),
- GPL/heavy-dep isolation behind the process boundary (consistent with espeak-ng today),
- the default ~250 MB local tier is untouched — the sidecar is an **opt-in tier**.

**Honest cost:** a frozen PyTorch binary is a different size class (GB-plus, and GPU-build-specific, like voicebox's
separately-versioned ~4 GB CUDA archive). It must be opt-in, and on CPU the heavy models will miss the live ≤1–1.5 s
first-audio budget — so they fit a **Studio generation/voiceover mode** better than the live pipeline unless on GPU.
This is why VLS-002 (GPU) sequences before VVL-002.

---

## Work items

> IDs are **provisional** (slotted onto existing prefixes VLS/VVL/VDX/VST). Each becomes a `docs/specifications/*.html`
> spec + a ROADMAP entry once the sequence is approved. Every item obeys the cross-cutting invariants at the bottom.

### VLS-002 · **[Core]** — Local GPU acceleration + STT catalog expansion  *(quick win, foundation)*
- **Goal:** unlock accuracy and make heavy models viable for live use.
- **Scope (in):**
  - Add Whisper models to `WhisperCppModelCatalog`: `medium`, `large-v3`, `large-v3-turbo` (+ `.en`/`q5_1` where they exist), each with real SHA-256 + size from HF LFS metadata.
  - Add a GPU execution seam for STT: Whisper.net runtime EPs (CUDA / CoreML / Vulkan) selected by `Voxa:WhisperCpp:Device`, **CPU default**, GPU runtime packages **opt-in** (user-added, not bundled — sidesteps the native re-bundling issue from commit `d2a11ae`).
- **Scope (out):** streaming STT (P0); distil / faster-whisper (follow-up); **Kokoro / ONNX-RT GPU** (deferred — the ORT CPU and GPU packages conflict in-process, and Kokoro is already acceptable on CPU; revisit on demand).
- **Key files:** `WhisperCppModelCatalog.cs`, `WhisperCppSttEngine.cs`, `WhisperCppDescriptors.cs` (validation of the new device key), Kokoro engine; runtime package refs.
- **Risks:** GPU runtime packaging must not re-bundle natives (cf. commit `d2a11ae`); large models (1.5–3 GB) are only practical on GPU — gate guidance in docs. Hashes must be fetched at build time (not fabricated).
- **Tests:** catalog round-trip + offline error text (default suite); GPU path behind `Category=LocalModels`, hardware-gated.
- **Effort:** ~3–4 d.

### VVL-002 · **[Core + Studio]** — Local expressive / cloning TTS via frozen Python sidecar  *(strategic centerpiece)*
- **Goal:** fill VVL-001's deferred **local** cloning slot **and** add expressive, multilingual TTS breadth, in one vehicle.
- **Boundary:** the engine + local `IVoiceCloneProvider` ship as **Core** provider packages (the SDK/CLI may compose them in a pipeline if GPU/latency allows). Their heavy, non-real-time, generation-oriented use is surfaced in **Studio**'s voice-artifacts workbench — that is where these models earn their keep. Core's recommended real-time TTS stays Piper/Kokoro/cloud.
- **Scope (in):**
  - A new `Voxa.Speech.<Sidecar>` package: a `PyInstaller`-frozen engine binary, pinned per-platform in a catalog (`VoxaModelArtifact`, `Executable`), driven by a `PiperProcessHost`-style host with **streaming PCM** over the IPC contract (to satisfy `ITextToSpeechEngine.SynthesizeAsync` yielding).
  - An `ITextToSpeechEngine` impl + `VoxaTtsDescriptor` (with `WarmUpAsync`).
  - A **local** `IVoiceCloneProvider` wired via `ResolveCloner` → the existing Studio Voices wizard lights up its "coming soon" local option.
  - Expressive control: start with **inline-tag passthrough** (`[laugh]`, etc.) + per-voice style in config; defer a richer prosody frame.
- **Candidate models:** XTTS-v2 or Chatterbox (cloning **and** expressive, multilingual) host the engine; OpenVoice as the tone-color converter for the lightest cloning path. **Gated by a quality/latency spike** (the exact blocker VVL-001 named).
- **Scope (out):** a prosody/instruct frame contract (separate, later); >N languages beyond the chosen model's set.
- **Key files:** new package; `IVoiceCloneProvider`/`IVoiceCatalogProvider` (existing); descriptor registration; new pinned catalog; (maybe) `ITextToSpeechEngine` options extension in Abstractions.
- **Risks:** frozen-binary build + CI per platform × accelerator; multi-GB download (opt-in tier); CPU latency (Studio-generation fit, not live, unless GPU); license review of the bundled model weights + Python deps; reproducible freeze.
- **Tests:** IPC/streaming contract + clone round-trip behind `Category=LocalModels`; license-closure gate test (no GPL leak into managed assembly, mirroring the Kokoro gate).
- **Effort:** ~2–3 weeks (spike ~3 d → build/CI ~1 w → engine + clone provider + tests ~1 w).

### VLS-003 · **[Core]** — Local LLM agent provider (Ollama)  *(leapfrog — voicebox can't do this)*
- **Goal:** complete the fully-keyless local conversation loop (WhisperCpp → local LLM → Piper/Kokoro). Today the only keyless agent is `Echo`.
- **Scope (in):** an `Ollama` provider branch in `DefaultAgentFactory` (reuse the OpenAI-compatible base-url path against `http://localhost:11434/v1`, or `OllamaSharp`'s `IChatClient`); a profile; startup guidance/validation when Ollama is unreachable; docs recipe.
- **Scope (out):** bundling/pinning model weights (Ollama owns its own pull); in-process llama.cpp.
- **Key files:** `DefaultAgentFactory` / `VoxaDefaults.cs`; meta-package config; a keyless conversation test.
- **Risks:** Ollama is an external daemon (not air-gap-bundled) — be explicit it's a *bring-your-own-runtime* local option, distinct from the SHA-pinned speech tier.
- **Tests:** factory branch unit test; an end-to-end keyless conversation test gated appropriately (Ollama may be unavailable in CI → trait-gate or mock the endpoint).
- **Effort:** ~2–3 d.

### VDX-002 · **[Core / headless]** — Voxa MCP server ("give your agent a voice / ears")  *(distribution wedge)*
- **Goal:** expose Voxa's speech tier as MCP tools any coding agent (Claude Code, Cursor) can call — voicebox's cleverest distribution move, and a better fit for Voxa given the agent-loop story.
- **Scope (in):** a `Voxa.Mcp` package/sample using the official C# MCP SDK; tools `voxa.speak` (text→audio), `voxa.transcribe` (audio→text), optionally `voxa.converse`; composes the existing local tier so it runs **keyless**.
- **Scope (out):** full duplex conversation over MCP; auth/multi-tenant.
- **Key files:** new `Voxa.Mcp` package; reuse `ITextToSpeechEngine`/`ISpeechToTextEngine` + descriptors.
- **Risks:** MCP transport/lifecycle; audio return shape (file vs base64 vs stream).
- **Tests:** tool-contract tests with a faked engine (no cloud, no model download in the default lane).
- **Effort:** ~3–4 d.

### VDX-003 · **[Core]** — `voxa` command-line interface  *(makes the Core/Studio split concrete)*
- **Goal:** give Core a first-class headless entry point — the CLI half of "Core = CLI + SDK."
- **Scope (in):**
  - `voxa run <appsettings.json>` — compose and run a pipeline headlessly via the transport-agnostic `DefaultVoicePipelineComposer.Compose(IServiceProvider)`, against files or (where available) system audio.
  - `voxa transcribe <wav>` — STT one file → text (cross-platform, no audio device needed).
  - `voxa say "<text>" [-o out.wav]` — TTS to a file (cross-platform).
  - `voxa models {list|prefetch|verify|purge}` — drive `VoxaModelCache` (the VLS-001-deferred CLI, now with a scenario).
- **Scope (out):** live mic conversation on non-Windows (blocked on a cross-platform console audio backend — live `run` reuses Studio's WASAPI device on Windows; file-driven commands are cross-platform); anything interactive/GUI (that's Studio).
- **Key files:** new `src/Voxa.Cli` (a `dotnet tool`, `dotnet tool install -g Voxa.Cli`); reuses `AddVoxa`, the composer, `VoxaModelCache`; plain `ServiceCollection` per the config-capture rule (like Studio). Must not pull in ASP.NET.
- **Risks:** cross-platform console audio (keep live `run` Windows-first, file commands everywhere); staying on the transport-agnostic composer so the CLI doesn't drag in the web stack.
- **Tests:** command parsing; a file-in/text-out `transcribe` test on the local tier (`Category=LocalModels`); `models list` against a faked catalog.
- **Effort:** ~3–4 d.

### VST-004 · **[Studio]** — Studio dictation mode + UI-ergonomics polish
- **Goal:** broaden Studio beyond live-conversation with voicebox's best ergonomics.
- **Scope (in):**
  - **Global push-to-talk dictation:** system-wide hotkey (Windows) → capture via `WasapiAudioDevice` → WhisperCpp → emit/copy text.
  - **Floating session pill:** small always-on-top window walking `listening → transcribing → (agent) → speaking`, driven by the existing `VoxaDiagnosticsHub` event stream.
  - **Grey-out-incompatible + auto-switch** selectors in Config (extends VST-003's activated/local filtering with an explicit "incompatible because…" affordance).
  - **Provenance** on Metrics artifacts (exact model + flags + seed for reproducible re-runs).
- **Scope (out):** non-Windows global hotkey; cross-platform secrets (already deferred).
- **First step of a broader Studio voice-artifacts track** (future, out of this program's scope but on the Studio roadmap, all **[Studio]** per the boundary): podcast time-stretch / speed-up, voiceover & batch synthesis (home for VVL-002's heavy models), an effects chain, and a stories/timeline editor — the rest of voicebox's surface.
- **Key files:** `apps/Voxa.Studio` Views/ViewModels (new dictation + pill), `ConfigViewModel`, `MetricsViewModel`/`RunBundle`, `IStudioAudioDevice`.
- **Risks:** global hotkey + always-on-top are OS-specific; keep view models Avalonia-free + headless-testable; honor the no-Generic-Host rule and `voxa-design` tokens.
- **Effort:** ~1–1.5 weeks.

---

## Sequence & milestones

Order is by value/risk, not hard technical deps — VVL-002's sidecar bundles its own
accelerator, so it does **not** wait on VLS-002's GPU work.

```
M1   VLS-002   GPU + Whisper catalog .............. [Core]          quick, low-risk
M2   VDX-002   Voxa.Mcp server .................... [Core]    ┐
     VDX-003   voxa CLI ............................ [Core]    ├ parallel · independent · keyless
     VLS-003   Ollama local LLM .................... [Core]    ┘
M3   VVL-002   expressive/cloning sidecar ..... [Core+Studio]  spike-gated · self-contained GPU
M4   VST-004   Studio dictation + polish .......... [Studio]       rides the rest
```

- **M1 — Foundations & quick win:** VLS-002. ~4 d. De-risks heavy STT.
- **M2 — Headless Core surfaces & leapfrog (parallel):** VDX-002 (MCP) + VDX-003 (CLI) + VLS-003 (Ollama). ~1 wk wall-clock. All independent, all keyless, all sharpen the Core identity.
- **M3 — Strategic centerpiece:** VVL-002. ~2–3 wk, **spike-gated**. The big bet; the sidecar bundles its own GPU runtime, so it does **not** depend on VLS-002.
- **M4 — Studio polish:** VST-004. ~1–1.5 wk. Rides the rest.

Rough total: ~6 weeks of focused work, front-loaded with cheap wins so value ships before the big bet.

## Cross-cutting invariants (every item)

- `Voxa.Core` stays zero-dependency (NUlid only). Any TTS-contract extension goes in **Abstractions**, never Core.
- Every model/binary (incl. the frozen sidecar) is **SHA-256-pinned** in a catalog; bumping a pin is a code change with a test, never a floating reference.
- GPL / heavy deps stay **behind a process boundary**; add a license-closure gate test (mirror the Kokoro one).
- Any test that downloads models carries `[Trait("Category", "LocalModels")]`; the default lane stays offline.
- Config-capture rule: capture the `IConfiguration` passed to `AddVoxa`; never `GetRequiredService<IConfiguration>()`.
- Studio: no Generic Host; view models Avalonia-free + headless-testable; `IStudioAudioDevice` seam; `voxa-design` tokens.
- Per PR: one logical change, branch from `main`, `[Unreleased]` CHANGELOG entry, warnings-as-errors clean, a `docs/specifications/*.html` spec authored before the item's code.

## Decisions (all resolved 2026-06-18)

1. **Sidecar host model (VVL-002):** ✅ **frozen PyInstaller binary**, pinned as a `VoxaModelArtifact` and driven by the Piper-style child-process host. Keeps the project .NET-first; the sidecar is an opt-in adjunct, not a Python rewrite.
2. **Core vs Studio boundary:** ✅ established above — **Core stays pipeline-pure** (SDK + CLI, providers only); **Studio** owns the voice-artifacts workbench (generation, cloning UI, dictation, future podcast speed-up / voiceover / effects).
3. **Sidecar primary model (VVL-002):** ✅ **spike XTTS-v2 vs OpenVoice** — XTTS-v2 for cloning + expressive multilingual, OpenVoice for the lightest cloning path; the spike picks the winner (or both) on quality / latency / licence before any engine code.
4. **MCP surface (VDX-002):** ✅ first-class **`Voxa.Mcp` NuGet** — a headless Core-side surface, peer to the ASP.NET integration.
5. **CLI (VDX-003):** ✅ promote a first-class **`voxa` CLI** to a work item — Core's headless entry point (see VDX-003 above). Gives the "Core = CLI + SDK" framing a concrete surface and absorbs VLS-001's deferred `voxa models` CLI.

_Tracked as session tasks #1–#6 (VLS-002, VDX-002, VDX-003, VLS-003, VVL-002, VST-004)._
