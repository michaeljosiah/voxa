# Voxa.Samples.AspNetServer

A multi-vendor voice-agent server with an in-browser demo client. WebSocket endpoints, each composing a different pipeline shape — including a side-by-side comparison of the lower-level `Pipeline.Build()` API and the fluent `MapVoxaVoice` surface from `Voxa.AspNetCore`.

## Endpoints

| Route | Pipeline | Surface | Vendors |
|-------|----------|---------|---------|
| `/voice/voice-live`         | `WebSocketAudioSource → AzureVoiceLiveProcessor → WebSocketAudioSink` (full LLM-driven composite) | Lower-level | Azure Voice Live (or any OpenAI Realtime endpoint) |
| `/voice/azure`              | `... → AzureSpeechStt → Echo → AzureSpeechTts → ...` | Lower-level | Azure Speech (STT + TTS) |
| `/voice/openai`             | `... → OpenAIWhisperStt → Echo → OpenAITts → ...` | Lower-level | OpenAI (STT + TTS) |
| `/voice/openai-realtime`    | `... → OpenAIRealtimeProcessor → ...` | Lower-level | OpenAI Realtime |
| `/voice/openai-batch`       | `... → Whisper STT → MAF agent → SentenceAggregator → OpenAI TTS → ...` | Lower-level | OpenAI |
| `/voice/openai-batch-fluent` | Same chain as `/voice/openai-batch`, expressed via `MapVoxaVoice` | **Fluent** (`Voxa.AspNetCore`) | OpenAI |
| `/voice/azure-elevenlabs`   | `... → AzureSpeechStt → Echo → ElevenLabsTts → ...` | Lower-level | Azure Speech STT, ElevenLabs TTS |
| `/voice/azure-mistral`      | `... → AzureSpeechStt → Echo → MistralTts → ...` | Lower-level | Azure Speech STT, Mistral Voxtral-TTS |

The "Echo" processor is a tiny demo adapter that forwards each final `TranscriptionFrame` as a `TextFrame`. **In a real granular pipeline, replace it with `MicrosoftAgentVoice.CreateProcessor(yourAgent)`** — the agent loop, frontend tool round-trips, token aggregation, and re-run logic are then handled for you:

```csharp
.Then(AzureSpeech.StreamingTranscription(azure))
.Then(MicrosoftAgentVoice.CreateProcessor(yourAgent))    // ← swap Echo for this
.Then(new SentenceAggregator())
.Then(ElevenLabs.Synthesis(elevenlabs))
```

## Two integration surfaces, side-by-side

The `/voice/openai-batch` and `/voice/openai-batch-fluent` endpoints are **the same pipeline** built two different ways. Compare them in [`Program.cs`](Program.cs) to see the boilerplate the fluent surface saves you.

Lower-level (`Pipeline.Build()`):

```csharp
app.Map("/voice/openai-batch", async (HttpContext ctx) =>
{
    if (!await EnsureWebSocketAsync(ctx)) return;
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();

    var pipeline = Pipeline.Build()
        .Source(new WebSocketAudioSource(ws, ...))
        .Then(MakeVad(...))
        .Then(OpenAISpeech.StreamingTranscription(openai))
        .Then(new TranscriptionFilter())
        .Then(MicrosoftAgentVoice.CreateProcessor(agent, ...))
        .Then(new SentenceAggregator())
        .Then(OpenAISpeech.Synthesis(openai))
        .Sink(new WebSocketAudioSink(ws));

    await using var runner = new PipelineRunner(pipeline, ctx.RequestAborted);
    await runner.StartAsync(ct: ctx.RequestAborted);
    await runner.WaitAsync().WaitAsync(ctx.RequestAborted);
});
```

Fluent (`MapVoxaVoice`):

```csharp
app.MapVoxaVoice("/voice/openai-batch-fluent", voice => voice
    .UseProcessor(_ => MakeVad(...))
    .UseSpeechToText(() => OpenAISpeech.StreamingTranscription(openai))
    .UseTranscriptionFilter()
    .UseProcessor(ctx => MicrosoftAgentVoice.CreateProcessor(agent, ...))
    .UseSentenceAggregator()
    .UseTextToSpeech(() => OpenAISpeech.Synthesis(openai)));
```

## Configure (User Secrets — recommended)

The csproj has a `<UserSecretsId>` so secrets are stored outside the repo:

```bash
# Voice Live
dotnet user-secrets set "AzureVoiceLive:Endpoint" "wss://<resource>.cognitiveservices.azure.com/voice-live/realtime?model=gpt-realtime-mini&api-version=2025-10-01" --project samples/Voxa.Samples.AspNetServer
dotnet user-secrets set "AzureVoiceLive:ApiKey"   "<your-key>" --project samples/Voxa.Samples.AspNetServer

# Granular STT
dotnet user-secrets set "AzureSpeech:SubscriptionKey" "<your-key>" --project samples/Voxa.Samples.AspNetServer
dotnet user-secrets set "AzureSpeech:Region"          "eastus"     --project samples/Voxa.Samples.AspNetServer

# OpenAI
dotnet user-secrets set "OpenAI:ApiKey" "sk-..." --project samples/Voxa.Samples.AspNetServer

# ElevenLabs
dotnet user-secrets set "ElevenLabs:ApiKey"  "<your-key>" --project samples/Voxa.Samples.AspNetServer
dotnet user-secrets set "ElevenLabs:VoiceId" "21m00Tcm4TlvDq8ikWAM" --project samples/Voxa.Samples.AspNetServer

# Mistral
dotnet user-secrets set "Mistral:ApiKey" "<your-key>" --project samples/Voxa.Samples.AspNetServer
```

List what's set: `dotnet user-secrets list --project samples/Voxa.Samples.AspNetServer`.
Clear everything: `dotnet user-secrets clear --project samples/Voxa.Samples.AspNetServer`.

User Secrets only load in `Development` (which the `http` launch profile sets). For production you'd switch to env vars, Azure Key Vault, etc.

### Or env vars (no setup, double-underscore = colon)

```bash
export AzureVoiceLive__ApiKey="<key>"
export AzureSpeech__SubscriptionKey="<key>"
export AzureSpeech__Region="eastus"
# etc.
```

## Run

```bash
dotnet run --project samples/Voxa.Samples.AspNetServer --launch-profile http
```

Open <http://localhost:5009/> in Chrome (mic permission required). Pick a route, click **Start**, talk. The page captures your mic, ships PCM to the server, plays the server's PCM response back, and prints transcription / text envelopes to the log.

## What's in the in-browser client

- `wwwroot/index.html` — UI, WebSocket lifecycle, Web Audio playback scheduler.
- `wwwroot/recorder-worklet.js` — `AudioWorklet` that buffers Float32 mic samples into 50 ms chunks and converts to 16-bit PCM.

The page asks `AudioContext` for the route's target sample rate (24 kHz for Voice Live, 16 kHz for granular). The browser resamples the mic stream automatically.

## Wire protocol

Per [`Voxa.Transports.WebSocket.Protocol.WireProtocol`](../../src/Voxa.Transports.WebSocket/Protocol/WireProtocol.cs):

**Client → Server:**
- Binary: 16-bit PCM at the per-route sample rate
- Text JSON: `{"type":"hello",...}`, `{"type":"end"}`, `{"type":"toolResult",...}`, `{"type":"text",...}`

**Server → Client:**
- Binary: 16-bit PCM (response audio)
- Text JSON: `{"type":"transcription",...}`, `{"type":"text",...}`, `{"type":"toolCall",...}`, `{"type":"speaking",...}`, `{"type":"interruption"}`, `{"type":"status",...}`, `{"type":"error",...}`, `{"type":"end"}`

The `status` envelope is for sanitized backend-tool progress (e.g. *"Checking your spending..."*). The MAF adapter emits it via `StatusFrame` whenever a host has configured `MicrosoftAgentVoiceOptions.BuildBackendToolStatus` for the called tool.

## Mix-and-match recipe

The whole point of the multi-vendor split: **each STT engine works with each TTS engine, and dropping in `MicrosoftAgentVoice.CreateProcessor` slots a real LLM into the middle.** That's 4 STT × N TTS × any MAF agent = a lot of ground covered by a few small composable processors.
