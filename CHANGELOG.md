# Changelog

All notable changes to Voxa are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). Pre-1.0 the public API may change between minor versions.

## [Unreleased]

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

[Unreleased]: https://github.com/michaeljosiah/voxa/compare/v0.2.0-alpha...HEAD
[0.2.0-alpha]: https://github.com/michaeljosiah/voxa/compare/v0.1.0-alpha.2...v0.2.0-alpha
[0.1.0-alpha.2]: https://github.com/michaeljosiah/voxa/compare/v0.1.0-alpha...v0.1.0-alpha.2
[0.1.0-alpha]: https://github.com/michaeljosiah/voxa/releases/tag/v0.1.0-alpha
