# Voxa.Services.OpenAIRealtime

`OpenAIRealtimeProcessor` — a single drop-in `FrameProcessor` that bridges your Voxa pipeline to the **OpenAI Realtime API**. Full-duplex streaming, server-side voice activity detection, native interruption, sub-400 ms turn-taking. The C# equivalent of Pipecat's `OpenAIRealtimeBetaService`.

## Why use this instead of Whisper + TTS chained?

Chaining `Whisper REST → LLM → TTS REST` accumulates 1.3–1.9 s of latency per turn and Whisper hallucinates words from breath/silence. The Realtime API solves both at once:

| | `Whisper REST + TTS REST` chain | `OpenAIRealtimeProcessor` |
|---|---|---|
| Time to first audio | 1.3–1.9 s | 250–400 ms |
| Hallucinations on silence | Frequent | None — server VAD gates speech |
| Interruption | Manual gate, easy to mistime | Native, server-driven |
| API calls per turn | 2× HTTP | 1× WebSocket session |

## Install

```bash
dotnet add package Voxa.Services.OpenAIRealtime
```

## Quickstart

```csharp
using Voxa.Pipelines;
using Voxa.Services.OpenAIRealtime;
using Voxa.Transports.WebSocket;

var pipeline = Pipeline.Build()
    .Source(new WebSocketAudioSource(clientWs))
    .Then(new OpenAIRealtimeProcessor(new OpenAIRealtimeOptions
    {
        ApiKey = builder.Configuration["OpenAI:ApiKey"]!,
        Model = "gpt-realtime-mini",
        Voice = "alloy",
        Instructions = "You are a friendly voice assistant. Keep responses brief.",
    }))
    .Sink(new WebSocketAudioSink(clientWs));

await using var runner = new PipelineRunner(pipeline);
await runner.StartAsync();
await runner.WaitAsync();
```

That's the whole thing. No `SilenceGate`, no separate STT, no separate TTS — the Realtime API handles all of it server-side.

## Configuration

| Option | Default | Notes |
|---|---|---|
| `ApiKey` | (required) | Sent as `Authorization: Bearer <key>`. |
| `Endpoint` | `wss://api.openai.com/v1/realtime` | Override only when proxying. The model is appended as `?model=...` automatically. |
| `Model` | `gpt-realtime-mini` | Or `gpt-realtime`, `gpt-4o-realtime-preview`. |
| `Voice` | `alloy` | One of `alloy`, `ash`, `ballad`, `coral`, `echo`, `sage`, `shimmer`, `verse`. |
| `Instructions` | `null` | System prompt. |
| `TurnDetection.Threshold` | `0.5` | Server-VAD activation probability. Lower = more sensitive. |
| `TurnDetection.PrefixPaddingMs` | `300` | Audio prepended to detected speech for context. |
| `TurnDetection.SilenceDurationMs` | `500` | Sustained silence before turn-end. |
| `Tools` | `[]` | Function tools the model can invoke. Wire results back via `ToolCallResultFrame`. |
| `InputSampleRate` / `OutputSampleRate` | `24000` | The Realtime API expects pcm16 @ 24 kHz. |

## Tool calling

The processor emits a `ToolCallRequestFrame` when the model wants to invoke a tool. Your downstream processor handles it and pushes back a `ToolCallResultFrame` (e.g. via the `MicrosoftAgentsProcessor` adapter or your own dispatcher) — the processor forwards that back into the session and asks the model to continue.

## Auth note

The transport sends both `Authorization: Bearer <ApiKey>` and `OpenAI-Beta: realtime=v1`. If you proxy through a gateway that strips the `OpenAI-Beta` header, the connection will fail.

## License

MIT.
