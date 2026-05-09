# Voxa

[![CI](https://github.com/michaeljosiah/voxa/actions/workflows/ci.yml/badge.svg)](https://github.com/michaeljosiah/voxa/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)

A frame-based, real-time voice AI pipeline framework for .NET. Inspired by [Pipecat](https://github.com/pipecat-ai/pipecat); built around the Microsoft Agent Framework, Azure Voice Live, and Azure Speech.

> **Status: pre-alpha (0.1.x).** Public API stabilising. Not yet on NuGet.

## What it is

Voxa lets you compose real-time voice agents from small, testable processors. Each processor consumes and emits typed `Frame`s — audio, transcription, tool calls, control signals. System frames (interruption, errors) preempt data frames in their own task. Pipelines run asynchronously with bounded backpressure on data, unbounded priority on system signals.

```csharp
var pipeline = Pipeline.Build()
    .Source(new WebSocketAudioSource(ws))
    .Then(new AzureVoiceLiveProcessor(opts))
    .Sink(new WebSocketAudioSink(ws));

await using var runner = new PipelineRunner(pipeline);
await runner.StartAsync();
await runner.WaitAsync();
```

## Why not just call Voice Live (or OpenAI Realtime) directly

Voice Live is great for the simple case. But the moment you have a second backend (regional fallback, premium voices Voice Live doesn't host, OpenAI Realtime, telephony), one tenant policy that diverges, or an audit/cost/observability concern that crosses backends, you need a pipeline. Voxa is that pipeline. The Voice Live composite processor is one node in it; you can swap it for an Azure Speech STT → MAF agent → Azure Speech TTS chain on the same wire.

The same `AzureVoiceLiveProcessor` speaks **Azure Voice Live**, **Azure OpenAI Realtime**, and **OpenAI Realtime** — they share a wire protocol, so only the endpoint URL and auth header change.

## Packages

### Core

| Package | Description |
|---------|-------------|
| `Voxa.Core` | Frames, processors, pipeline, runner. Zero external deps beyond NUlid. |
| `Voxa.Testing` | WAV file source/sink, capturing/passthrough processors. |
| `Voxa.Transports.WebSocket` | Host-agnostic source + sink over `System.Net.WebSockets.WebSocket`. |
| `Voxa.Services.AzureVoiceLive` | Composite STT+LLM+TTS+VAD via Azure Voice Live's Realtime API. |
| `Voxa.Services.OpenAIRealtime` | Composite STT+LLM+TTS+VAD via OpenAI Realtime API (full-duplex, server-side VAD). |
| `Voxa.Services.MicrosoftAgents` | Wraps any Microsoft Agent Framework `AIAgent` as a processor. |
| `Voxa.Observability` | `TracingProcessor` + `VoxaActivities` ActivitySource for OpenTelemetry. |

### Speech (granular STT/TTS, multi-vendor)

| Package | STT | TTS | Description |
|---------|-----|-----|-------------|
| `Voxa.Speech.Abstractions` | — | — | `ISpeechToTextEngine`, `ITextToSpeechEngine`, generic `SpeechToTextProcessor` / `TextToSpeechProcessor`, `SilenceGateProcessor` (energy VAD). |
| `Voxa.Speech.Azure` | ✅ | ✅ | Azure Cognitive Services Speech SDK. |
| `Voxa.Speech.OpenAI` | ✅ | ✅ | Whisper REST + OpenAI TTS (`/v1/audio/speech`). Works against OpenAI-compatible proxies. |
| `Voxa.Speech.ElevenLabs` | — | ✅ | Streaming TTS, voice cloning, voice settings. |
| `Voxa.Speech.Mistral` | — | ✅ | Voxtral-TTS via Mistral's OpenAI-compatible audio API. |

### Audio

| Package | Description |
|---------|-------------|
| `Voxa.Audio.SileroVad` | ML-based VAD using the bundled Silero VAD v5 ONNX model. Drop-in replacement for `SilenceGateProcessor` for noisy environments. |

Mix-and-match: use any STT vendor with any LLM with any TTS vendor.

## Architecture

```
┌──────────────┐   ┌──────────────┐   ┌──────────────┐
│PipelineSource│-->│FrameProcessor│-->│ PipelineSink │
└──────────────┘   └──────────────┘   └──────────────┘
       │                  ↑                  │
   IngestAsync       ErrorFrame         ReadAllAsync
                    (upstream)
```

Each `FrameProcessor` runs two concurrent tasks: a **system task** draining priority frames (`InterruptionFrame`, speaking events, errors) and a **data task** draining ordered frames. An interruption mid-frame cancels the in-flight data frame's `CancellationToken` so long-running calls (LLM streaming, TTS synthesis) abort cleanly.

## Two pipeline shapes

**Voice Live path** — managed STT+LLM+TTS+VAD in a single processor:

```csharp
Pipeline.Build()
    .Source(new WebSocketAudioSource(ws))
    .Then(new AzureVoiceLiveProcessor(voiceLiveOpts))
    .Sink(new WebSocketAudioSink(ws));
```

**Granular path** — vendor-neutral STT + agent + TTS. Mix any STT, any LLM, any TTS:

```csharp
Pipeline.Build()
    .Source(new WebSocketAudioSource(ws))
    .Then(AzureSpeech.StreamingTranscription(azureOpts))     // STT vendor
    .Then(new MicrosoftAgentsProcessor(yourAgent))           // any MAF agent
    .Then(ElevenLabs.Synthesis(elevenlabsOpts))              // TTS vendor
    .Sink(new WebSocketAudioSink(ws));
```

## Vendor recipes

Each STT vendor pairs with each TTS vendor pairs with any agent. Some examples:

```csharp
// Azure end-to-end (cheapest, broadest regional coverage)
.Then(AzureSpeech.StreamingTranscription(azure))
.Then(new MicrosoftAgentsProcessor(agent))
.Then(AzureSpeech.Synthesis(azure))

// Whisper STT, OpenAI TTS, OpenAI agent — full OpenAI stack
.Then(OpenAISpeech.StreamingTranscription(openai))
.Then(new MicrosoftAgentsProcessor(openaiAgent))
.Then(OpenAISpeech.Synthesis(openai))

// Premium voice — Whisper + ElevenLabs voice clone
.Then(OpenAISpeech.StreamingTranscription(openai))
.Then(new MicrosoftAgentsProcessor(agent))
.Then(ElevenLabs.Synthesis(elevenlabs))

// Cost-optimised — Azure STT (fast, cheap) + Mistral TTS (Voxtral)
.Then(AzureSpeech.StreamingTranscription(azure))
.Then(new MicrosoftAgentsProcessor(agent))
.Then(Mistral.Synthesis(mistral))
```

## Wire protocol

Binary WebSocket frames carry raw 16-bit PCM @ 24 kHz mono. Text WebSocket frames carry typed JSON envelopes:

**Client → Server:** `hello`, `end`, `text`, `toolResult`
**Server → Client:** `transcription`, `text`, `toolCall`, `speaking`, `interruption`, `error`, `end`

See [`WireProtocol.cs`](src/Voxa.Transports.WebSocket/Protocol/WireProtocol.cs) for the codec.

## Observability

Voxa.Observability publishes `VoxaActivities.Source` (an `ActivitySource` named `Voxa`). Drop a `TracingProcessor` anywhere in the pipeline to emit per-frame spans:

```csharp
Pipeline.Build()
    .Source(...)
    .Then(new TracingProcessor("user-input"))
    .Then(new AzureVoiceLiveProcessor(opts))
    .Then(new TracingProcessor("voice-live-out"))
    .Sink(...);
```

Wire OpenTelemetry to capture them:

```csharp
services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource("Voxa").AddOtlpExporter());
```

## Sample app

[`samples/Voxa.Samples.AspNetServer`](samples/Voxa.Samples.AspNetServer) — ASP.NET Core voice-agent server with **five WebSocket endpoints** demonstrating each pipeline shape:

| Route | Pipeline |
|-------|----------|
| `/voice/voice-live`         | Voice Live composite (full LLM-driven) |
| `/voice/azure`              | Azure STT → echo → Azure TTS |
| `/voice/openai`             | OpenAI Whisper → echo → OpenAI TTS |
| `/voice/azure-elevenlabs`   | Azure STT → echo → ElevenLabs TTS |
| `/voice/azure-mistral`      | Azure STT → echo → Mistral Voxtral-TTS |

`dotnet run --project samples/Voxa.Samples.AspNetServer`. Configure only the vendors you want to demo.

## Building

```bash
dotnet build
dotnet test
```

Targets `net10.0`. Requires .NET 10 SDK.

## Roadmap

| Phase | Scope |
|-------|-------|
| 1 | ✅ Core pipeline primitives |
| 2 | ✅ AzureVoiceLive composite + Voxa.Testing harness |
| 3 | ✅ WebSocket transport + Microsoft Agents adapter |
| 5 | ✅ AzureSpeech STT/TTS standalone + ASP.NET sample |
| **6 (current)** | Observability, OSS release, NuGet publish, CI |
| 4 | Mobile client integration (downstream consumers) |

(Phase 4 swapped to last since it lives in consuming repos, not Voxa itself.)

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). All contributions need an issue or design doc reference for non-trivial changes.

## License

MIT. See [LICENSE](LICENSE).
