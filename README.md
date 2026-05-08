# Voxa

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

A frame-based, real-time voice AI pipeline framework for .NET. Inspired by [Pipecat](https://github.com/pipecat-ai/pipecat); designed around Microsoft Agent Framework and Azure services.

> Pre-alpha. Phase 1 of 6 — core pipeline primitives. Not yet published to NuGet.

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
| **1 (current)** | Core pipeline primitives — frames, processors, pipeline, runner |
| 2 | `Voxa.Services.AzureVoiceLive` composite processor + offline test harness |
| 3 | `Voxa.Transports.WebSocket` + `Voxa.Services.MicrosoftAgents` adapter |
| 4 | Mobile transport integration |
| 5 | `Voxa.Services.AzureSpeech` STT/TTS + observability |
| 6 | OSS release, samples, docs |

## Building

```bash
dotnet build
dotnet test
```

Targets `net10.0`. Requires .NET 10 SDK.

## License

MIT. See [LICENSE](LICENSE).
