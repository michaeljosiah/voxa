# Voxa

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

A frame-based, real-time voice AI pipeline framework for .NET. Inspired by [Pipecat](https://github.com/pipecat-ai/pipecat); designed around Microsoft Agent Framework and Azure services.

> Pre-alpha. Phase 5 of 6 — Azure Speech STT/TTS standalone + first sample app landed. Not yet published to NuGet.

## What it is

Voxa lets you compose real-time voice agents from small, testable processors:

```csharp
var pipeline = Pipeline.Build()
    .Source(new WebSocketAudioSource(...))
    .Then(new AzureVoiceLiveProcessor(...))
    .Then(new MafToolDispatcher(...))
    .Sink(new WebSocketAudioSink(...));

await using var runner = new PipelineRunner(pipeline);
await runner.StartAsync();
await runner.WaitAsync();
```

Each processor consumes and emits typed `Frame`s — audio, transcription, tool calls, control signals. System frames (interruption, errors) preempt data frames in their own task. Pipelines run asynchronously with bounded backpressure on data and unbounded on system signals.

## Why not just call Voice Live directly

Voice Live is great. But the moment you have a second backend (regional fallback, premium voices that don't run on Voice Live, OpenAI Realtime, telephony), one tenant policy that diverges, or an audit/cost/observability concern that crosses backends, you need a pipeline. Voxa is that pipeline. The Voice Live composite processor is one node in it; you can swap it for an Azure Speech STT → MAF agent → Azure Speech TTS chain on the same wire.

## Architecture

```
┌──────────────┐   ┌─────────────┐   ┌──────────────┐
│ PipelineSource│-->│ FrameProcessor│-->│ PipelineSink │
└──────────────┘   └─────────────┘   └──────────────┘
       │                  ↑                  │
   IngestAsync      ErrorFrame           ReadAllAsync
                  (upstream)
```

Each `FrameProcessor` runs two concurrent tasks: a system task draining priority frames (`InterruptionFrame`, `ErrorFrame`, speaking events) and a data task draining ordered frames. An interruption mid-frame cancels the in-flight data frame's `CancellationToken` so long-running calls (LLM streaming, TTS synthesis) abort cleanly.

## Roadmap

| Phase | Scope |
|-------|-------|
| 1 | ✅ Core pipeline primitives — frames, processors, pipeline, runner |
| 2 | ✅ `Voxa.Services.AzureVoiceLive` composite processor + `Voxa.Testing` offline harness |
| 3 | ✅ `Voxa.Transports.WebSocket` + `Voxa.Services.MicrosoftAgents` adapter |
| 5 | ✅ `Voxa.Services.AzureSpeech` STT/TTS standalone + `Voxa.Samples.AspNetServer` |
| **4 (next)** | Mobile transport integration (downstream consumers — Payabo, etc.) |
| 6 | Observability, OSS release, NuGet publish, docs |

The same `AzureVoiceLiveProcessor` speaks Azure Voice Live, Azure OpenAI Realtime, **and** OpenAI Realtime — they share a wire protocol, so only the endpoint URL and auth header change. For tenants without Voice Live regional access, swap to the granular `AzureSpeechSttProcessor → MicrosoftAgentsProcessor → AzureSpeechTtsProcessor` chain.

The same `AzureVoiceLiveProcessor` speaks Azure Voice Live, Azure OpenAI Realtime, **and** OpenAI Realtime — they share a wire protocol, so only the endpoint URL and auth header change.

## Building

```bash
dotnet build
dotnet test
```

Targets `net10.0`. Requires .NET 10 SDK.

## License

MIT. See [LICENSE](LICENSE).
