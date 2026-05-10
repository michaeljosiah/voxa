# Voxa

[![CI](https://github.com/michaeljosiah/voxa/actions/workflows/ci.yml/badge.svg)](https://github.com/michaeljosiah/voxa/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)

A frame-based, real-time voice AI pipeline framework for .NET. Inspired by [Pipecat](https://github.com/pipecat-ai/pipecat); built around the Microsoft Agent Framework, Azure Voice Live, and Azure Speech.

> See [`ROADMAP.md`](ROADMAP.md) for tracked work — latency improvements, smart turn detection, echo suppression, AONIK integration.

> **Status: pre-alpha (0.1.x).** Public API stabilising. Not yet on NuGet.

## What it is

Voxa lets you compose real-time voice agents from small, testable processors. Each processor consumes and emits typed `Frame`s — audio, transcription, tool calls, control signals. System frames (interruption, errors) preempt data frames in their own task. Pipelines run asynchronously with bounded backpressure on data, unbounded priority on system signals.

The shortest path to a real voice endpoint, using the fluent ASP.NET Core surface (`Voxa.AspNetCore`):

```csharp
app.MapVoxaVoice("/voice", voice => voice
    .UseSpeechToText(() => OpenAISpeech.StreamingTranscription(openai))
    .UseTranscriptionFilter()
    .UseMicrosoftAgent(myAgent)
    .UseSentenceAggregator()
    .UseTextToSpeech(() => OpenAISpeech.Synthesis(openai)));
```

That's the whole endpoint. Per-connection lifecycle, WebSocket framing, deadlock-safe agent loop, frontend-tool round-trips, turn-boundary frames, and audit hooks are all wired for you.

The lower-level API is still there for hosts that want to build the pipeline by hand:

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
| `Voxa.Core` | Frames, processors, pipeline, runner, generic `AgentLoopProcessor`. Zero external deps beyond NUlid. |
| `Voxa.AspNetCore` | Fluent `MapVoxaVoice` + `VoicePipelineBuilder`. The recommended integration surface for ASP.NET Core hosts. |
| `Voxa.Testing` | WAV file source/sink, capturing/passthrough processors. |
| `Voxa.Transports.WebSocket` | Host-agnostic source + sink over `System.Net.WebSockets.WebSocket`. |
| `Voxa.Services.AzureVoiceLive` | Composite STT+LLM+TTS+VAD via Azure Voice Live's Realtime API. |
| `Voxa.Services.OpenAIRealtime` | Composite STT+LLM+TTS+VAD via OpenAI Realtime API (full-duplex, server-side VAD). |
| `Voxa.Services.MicrosoftAgents` | `MicrosoftAgentVoice.CreateProcessor(agent, options)` — wraps any MAF `AIAgent` as a configured `AgentLoopProcessor`. |
| `Voxa.Observability` | `TracingProcessor` + `VoxaActivities` ActivitySource for OpenTelemetry. |

### Speech (granular STT/TTS, multi-vendor)

| Package | STT | TTS | Description |
|---------|-----|-----|-------------|
| `Voxa.Speech.Abstractions` | — | — | `ISpeechToTextEngine`, `ITextToSpeechEngine`, generic `SpeechToTextProcessor` / `TextToSpeechProcessor`, `SilenceGateProcessor` (energy VAD), `TranscriptionFilter` (drops Whisper hallucinations), `SentenceAggregator` (LLM tokens → sentence-sized TTS chunks). |
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

## The agent loop

`Voxa.Core.AgentLoopProcessor` is a framework-agnostic per-turn agent processor. It owns the bookkeeping every voice-agent integration would otherwise re-implement:

- **Data-loop / turn-worker split** — the data loop never blocks on agent calls or tool round-trips, so frames keep flowing while a turn is running.
- **Per-turn id** + lifecycle frames (`LlmTurnStartedFrame` / `LlmTurnEndedFrame`) for clean turn boundaries downstream.
- **Frontend-tool TCS correlation** — tool calls round-trip through the pipeline (server emits `ToolCallRequestFrame`, client returns `ToolCallResultFrame`); the agent re-runs inline with the result appended.
- **Per-turn try/catch isolation** — a failed turn emits an upstream `ErrorFrame` and the worker drains the next queued transcription.
- **Token aggregation** — `LlmUsageFrame`s yielded by drivers roll into `TurnSummary.Usage` for hosts to record in `OnTurnCompleted`.

Hosts plug a runtime in by implementing `IAgentTurnDriver`. For Microsoft Agent Framework that's done for you — `MicrosoftAgentVoice.CreateProcessor(agent, options)` returns a fully-configured `AgentLoopProcessor`:

```csharp
voice.UseMicrosoftAgent(agent, options =>
{
    options.BuildMessages = (turn, ct) => LoadMyHistoryAsync(turn.UserText, ct);
    options.IsFrontendTool = name => myFrontendCatalog.Contains(name);
    options.BuildBackendToolStatus = name => name switch
    {
        "pf_get_spending_summary" => "Checking your spending...",
        _ => null,
    };
    options.OnTurnCompleted = (turn, summary, ct) => RecordAuditAsync(turn, summary, ct);
});
```

## Backend tool progress pattern

Voice agents commonly run a backend (read-only) tool mid-turn — "What are my top expenses?" → acknowledgement text → backend lookup → final answer text. Voxa supports this naturally:

1. The model streams the acknowledgement as `TextContent`. Voxa yields it as `LlmTextChunkFrame` immediately, so `SentenceAggregator` flushes the sentence and TTS starts speaking *before* the backend tool runs.
2. The model then emits a backend `FunctionCallContent`. MAF auto-executes the tool synchronously (raw tool names are never surfaced to the client).
3. While the tool runs, Voxa optionally emits a sanitized `StatusFrame("Checking your spending...")` for the client UI — opt-in via `MicrosoftAgentVoiceOptions.BuildBackendToolStatus`.
4. The model then streams the final answer text and any frontend display tools (`display_spending_pie_chart`, etc.) round-trip through the pipeline as normal.

The transport ships the status as `{ "type": "status", "message": "..." }` over the WebSocket. Hosts on a different transport can drop the frame or wrap it in their own envelope.

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
    .Then(MicrosoftAgentVoice.CreateProcessor(yourAgent))    // any MAF agent
    .Then(new SentenceAggregator())
    .Then(ElevenLabs.Synthesis(elevenlabsOpts))              // TTS vendor
    .Sink(new WebSocketAudioSink(ws));
```

## Vendor recipes

Each STT vendor pairs with each TTS vendor pairs with any agent. Some examples:

```csharp
// Azure end-to-end (cheapest, broadest regional coverage)
.Then(AzureSpeech.StreamingTranscription(azure))
.Then(MicrosoftAgentVoice.CreateProcessor(agent))
.Then(AzureSpeech.Synthesis(azure))

// Whisper STT, OpenAI TTS, OpenAI agent — full OpenAI stack
.Then(OpenAISpeech.StreamingTranscription(openai))
.Then(MicrosoftAgentVoice.CreateProcessor(openaiAgent))
.Then(OpenAISpeech.Synthesis(openai))

// Premium voice — Whisper + ElevenLabs voice clone
.Then(OpenAISpeech.StreamingTranscription(openai))
.Then(MicrosoftAgentVoice.CreateProcessor(agent))
.Then(ElevenLabs.Synthesis(elevenlabs))

// Cost-optimised — Azure STT (fast, cheap) + Mistral TTS (Voxtral)
.Then(AzureSpeech.StreamingTranscription(azure))
.Then(MicrosoftAgentVoice.CreateProcessor(agent))
.Then(Mistral.Synthesis(mistral))
```

## Wire protocol

Binary WebSocket frames carry raw 16-bit PCM @ 24 kHz mono. Text WebSocket frames carry typed JSON envelopes:

**Client → Server:** `hello`, `end`, `text`, `toolResult`
**Server → Client:** `transcription`, `text`, `toolCall`, `speaking`, `interruption`, `status`, `error`, `end`

`WebSocketAudioSink` accepts a `customSerializer` hook so hosts can add their own envelopes (e.g. AONIK's `threadReady`) without subclassing.

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

[`samples/Voxa.Samples.AspNetServer`](samples/Voxa.Samples.AspNetServer) — ASP.NET Core voice-agent server with WebSocket endpoints demonstrating each pipeline shape:

| Route | Pipeline | Surface |
|-------|----------|---------|
| `/voice/voice-live`         | Voice Live composite (full LLM-driven) | Lower-level `Pipeline.Build()` |
| `/voice/azure`              | Azure STT → echo → Azure TTS | Lower-level `Pipeline.Build()` |
| `/voice/openai`             | OpenAI Whisper → echo → OpenAI TTS | Lower-level `Pipeline.Build()` |
| `/voice/openai-realtime`    | OpenAI Realtime composite | Lower-level `Pipeline.Build()` |
| `/voice/openai-batch`       | Whisper → MAF agent → SentenceAggregator → OpenAI TTS | Lower-level `Pipeline.Build()` |
| `/voice/openai-batch-fluent` | Same as above, expressed via `MapVoxaVoice` | Fluent `Voxa.AspNetCore` |
| `/voice/azure-elevenlabs`   | Azure STT → echo → ElevenLabs TTS | Lower-level `Pipeline.Build()` |
| `/voice/azure-mistral`      | Azure STT → echo → Mistral Voxtral-TTS | Lower-level `Pipeline.Build()` |

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
| 5.5 | ✅ Generic `AgentLoopProcessor` + delegate-based MAF surface + fluent `MapVoxaVoice` |
| **6 (current)** | Observability, OSS release, NuGet publish, CI |
| 4 | Mobile client integration (downstream consumers) |

(Phase 4 swapped to last since it lives in consuming repos, not Voxa itself.)

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). All contributions need an issue or design doc reference for non-trivial changes.

## License

MIT. See [LICENSE](LICENSE).
