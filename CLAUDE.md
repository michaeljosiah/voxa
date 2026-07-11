# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Voxa is a frame-based, real-time voice AI pipeline framework for .NET 10 (inspired by Pipecat).
You compose voice agents from small, testable processors that consume and emit typed `Frame`s
(audio, transcription, tool calls, control signals). Pre-alpha; the public API still moves.

The solution is `Voxa.slnx` (the new XML solution format — there is no `.sln`). One `csproj` per
NuGet package under `src/`, one xUnit project per library under `tests/`, plus `apps/Voxa.Studio`
(an Avalonia desktop app), `samples/`, and `bench/`.

## Commands

```bash
# Build the whole solution (warnings are errors in src/, apps/, and most tests — fix, don't suppress)
dotnet build Voxa.slnx --configuration Release

# Everyday test run — EXCLUDES the model-download integration tests (keeps the loop fast & offline)
dotnet test Voxa.slnx --filter "Category!=LocalModels"

# One test project
dotnet test tests/Voxa.Core.Tests

# A single test (filter by fully-qualified name or substring)
dotnet test tests/Voxa.Core.Tests --filter "FullyQualifiedName~PipelineRunner_Drains"

# The local-speech integration tests (downloads SHA-256-pinned models on first run, then offline)
dotnet test Voxa.slnx --filter "Category=LocalModels"

# Run the Studio desktop app (Windows)
dotnet run --project apps/Voxa.Studio

# Run a sample server
dotnet run --project samples/Voxa.Samples.MinimalServer

# Benchmarks (BenchmarkDotNet)
dotnet run --project bench/Voxa.Benchmarks --configuration Release
```

`-warnaserror` matches CI behavior when you want it solution-wide:
`dotnet build Voxa.slnx -warnaserror`.

### The `LocalModels` test category is load-bearing

Tests tagged `[Trait("Category", "LocalModels")]` download real speech models (whisper.cpp, Piper,
Kokoro — ~hundreds of MB). They are **excluded from the default test run** and run in their own CI
lane with a cached, network-blocked model directory. Always use `--filter "Category!=LocalModels"`
for the inner loop; only run them when you've touched the speech engines or model cache. The cache
root is resolved via `VoxaModelCacheOptions` and honors the `VOXA_MODEL_CACHE` env var.

## Architecture — the big picture

### Frames and processors (`Voxa.Core`)

The pipeline is `PipelineSource → FrameProcessor… → PipelineSink`. The non-obvious part is the
**dual-task model inside every `FrameProcessor`**:

- A **system task** drains priority frames (`InterruptionFrame`, speaking events, `ErrorFrame`).
- A **data task** drains ordered frames (audio, transcription, text).

An interruption cancels the in-flight data frame's `CancellationToken`, so long-running work (LLM
streaming, TTS synthesis) aborts cleanly mid-call — **except** frames marked `IUninterruptible`
(tool calls, `EndFrame`), which are guaranteed to complete. The data loop is allocation-free at
steady state: the per-frame cancellation source is reused, replaced only after an interruption.

When adding a processor: forward unhandled frames downstream (especially `StartFrame`/`EndFrame`,
or the sink never completes), and accept the per-frame `CancellationToken` on any long-running work.

### Backpressure convention

Two regimes, by design:

- **Granular chain (processor→processor):** bounded `Wait` data channels (capacity 64, the
  `FrameProcessor` default) — a slow stage backpressures its upstream rather than dropping frames.
  System frames ride a separate unbounded priority channel.
- **Shedding paths:** `BoundedChannelFullMode.DropOldest` where dropping is the correct failure mode —
  the composite processors' audio channels (`AzureVoiceLiveProcessor`/`OpenAIRealtimeProcessor`/
  `SpeechToSpeechProcessor`) and the diagnostics hub's subscriber queues (`SeqNo` gaps signal drops
  intentionally). This is why a slow *audio/diagnostics consumer* drops data rather than stalling the
  pipeline.

Document any deviation.

### The agent loop (`Voxa.Core.AgentLoopProcessor`)

Framework-agnostic per-turn agent processor. The **data loop never blocks on agent/tool calls** —
a separate turn worker runs the turn while frames keep flowing. It owns per-turn ids, lifecycle
frames (`LlmTurnStartedFrame`/`LlmTurnEndedFrame`), frontend-tool TCS correlation (tool calls
round-trip through the pipeline as `ToolCallRequestFrame`/`ToolCallResultFrame`), per-turn
try/catch isolation, and token aggregation into `TurnSummary`. Hosts plug a runtime in via
`IAgentTurnDriver`; for Microsoft Agent Framework, `MicrosoftAgentVoice.CreateProcessor(agent, options)`
returns a fully-configured one.

### Two pipeline shapes

1. **Composite** — one processor wraps STT+LLM+TTS+VAD (`AzureVoiceLiveProcessor`,
   `OpenAIRealtimeProcessor`). Full-duplex, server-side VAD.
2. **Granular** — vendor-neutral chain: `STT → agent → SentenceAggregator → TTS`. Mix any STT
   vendor with any LLM with any TTS vendor. `SentenceAggregator` batches LLM tokens into
   sentence-sized TTS chunks; `TranscriptionFilter` drops Whisper hallucinations.

### The `AddVoxa` / registry / composer system (`Voxa.AspNetCore`)

This is the five-lines-to-a-voice-bot layer and the part most likely to surprise you:

- `AddVoxa(configuration)` (meta-package overload) registers a **provider registry** + every
  built-in provider, reads the `"Voxa"` config section, and adds a **fail-fast options validator**
  (`ValidateOnStart`) — the host refuses to start on an unknown provider/profile.
- `MapVoxaVoice("/voice").UseDefaults()` composes VAD → STT → agent → aggregation → TTS via
  `DefaultVoicePipelineComposer`, which is **transport-agnostic** (`Compose(IServiceProvider)`; the
  `HttpContext` overload forwards to it).
- Named **profiles** (`LowLatency`/`Quality`/`Cheap`) bundle the tuning knobs from
  `docs/performance-tuning.md`.
- **Config capture rule:** providers that need configuration capture the `IConfiguration` passed to
  `AddVoxa` — they must NOT call `GetRequiredService<IConfiguration>()`. ASP.NET registers
  `IConfiguration` implicitly, but plain `ServiceCollection` hosts (Voxa Studio, tests) do not, so
  DI resolution throws there. The validator, composer, and `DefaultAgentFactory` all follow this.

### Diagnostics hub (`Voxa.Core/Diagnostics`, enabled by `Voxa:Diagnostics:Enabled`)

`VoxaDiagnosticsHub` is a per-session typed event stream (VAD windows, turn edges, transcripts,
stage latencies). **Zero-cost when unobserved** (a `HasListeners` guard precedes every publish);
bounded drop-oldest channels per subscriber (`SeqNo` gaps signal drops intentionally). A
`StageLatencyTracker` inside the hub derives the per-turn latency waterfall and records the
`voxa.stage.latency` histogram. With diagnostics off, composed pipelines are byte-identical to
pre-diagnostics (golden-tested). This is what Voxa Studio's Talk view renders live.

### Voxa Studio (`apps/Voxa.Studio`)

Avalonia 11, Windows-first. Hosts the real composed pipeline in-process against the dev's mic/
speakers. Key facts for working in it:

- Uses a **plain `ServiceCollection` + `AddVoxa`, NOT the Generic Host** — hosted services (eager
  model warm-up) must not run; Studio never touches the network before the user acts.
- **View models are Avalonia-free** and headless-testable: a 33 ms `DispatcherTimer` calls
  `DrainPending()`; tests call it directly. Headless boot via `Avalonia.Headless.XUnit`.
- Audio is behind `IStudioAudioDevice` (WASAPI/NAudio on Windows, `NullAudioDevice` elsewhere/tests).
  WASAPI mix formats are usually `WAVE_FORMAT_EXTENSIBLE` — `CaptureFormatConverter` resolves the
  subformat GUIDs; an unconvertible format throws at session start rather than going silently deaf.
- Studio tests run both in the ubuntu default suite (headless) and a dedicated windows-latest CI lane.

## Conventions (from CONTRIBUTING.md)

- **`Voxa.Core` has zero external dependencies** beyond NUlid. Never add ASP.NET, Azure,
  OpenTelemetry, etc. references to it.
- **Each processor is testable in isolation** behind an interface (`ISpeechToTextEngine`,
  `IRealtimeApiTransport`). Don't burn cloud quota in unit tests — fake the engine/transport.
- Records for frames and config; `internal sealed` until a public surface is proven necessary; one
  short doc-comment line explaining *why* not *what*; no emojis in code.
- xUnit `using` comes from `<Using Include="Xunit" />` in the csproj (repo convention) — test files
  don't repeat it.
- Update `CHANGELOG.md` under `[Unreleased]`; branch from `main`; one logical change per PR.

## Reference docs

Design specs live in `docs/specifications/*.html` (developer-experience, local-speech, performance,
Studio). Guides: `docs/local-speech.md`, `docs/performance-tuning.md`, `docs/studio.md`,
`docs/background-agent.md` (the VDX-008 talker/thinker split). `ROADMAP.md`
tracks planned work and which items have shipped.
