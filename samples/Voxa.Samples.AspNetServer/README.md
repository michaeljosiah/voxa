# Voxa.Samples.AspNetServer

A multi-vendor voice-agent server demonstrating the full Voxa stack. Five WebSocket endpoints, each composing a different pipeline shape.

## Endpoints

| Route | Pipeline | Vendors involved |
|-------|----------|------------------|
| `/voice/voice-live`         | `WebSocketAudioSource → AzureVoiceLiveProcessor → WebSocketAudioSink` (full LLM-driven composite) | Azure Voice Live (or any OpenAI Realtime endpoint) |
| `/voice/azure`              | `WebSocketAudioSource → AzureSpeechStt → Echo → AzureSpeechTts → WebSocketAudioSink` | Azure Speech (STT + TTS) |
| `/voice/openai`             | `... → OpenAIWhisperStt → Echo → OpenAITts → ...` | OpenAI (STT + TTS) |
| `/voice/azure-elevenlabs`   | `... → AzureSpeechStt → Echo → ElevenLabsTts → ...` | Azure Speech STT, ElevenLabs TTS |
| `/voice/azure-mistral`      | `... → AzureSpeechStt → Echo → MistralTts → ...` | Azure Speech STT, Mistral Voxtral-TTS |

The "Echo" processor is a tiny demo adapter that forwards each final `TranscriptionFrame` as a `TextFrame` so the TTS speaks it back. **In a real granular pipeline, replace it with [`MicrosoftAgentsProcessor`](../../src/Voxa.Services.MicrosoftAgents/MicrosoftAgentsProcessor.cs)** wrapping any MAF `AIAgent`:

```csharp
.Then(AzureSpeech.StreamingTranscription(azure))
.Then(new MicrosoftAgentsProcessor(yourAgent))    // ← swap Echo for this
.Then(ElevenLabs.Synthesis(elevenlabs))
```

## Configure

Edit `appsettings.json` or set environment variables. Each endpoint only needs the vendor sections it actually uses, so you can configure just one and skip the rest.

```bash
# Voice Live (full composite agent)
export AzureVoiceLive__Endpoint="wss://<resource>.cognitiveservices.azure.com/voice-live/realtime?model=gpt-realtime-mini&api-version=2025-10-01"
export AzureVoiceLive__ApiKey="<your-key>"

# Granular paths
export AzureSpeech__SubscriptionKey="<your-key>"
export AzureSpeech__Region="eastus"

export OpenAI__ApiKey="sk-..."

export ElevenLabs__ApiKey="<your-key>"
export ElevenLabs__VoiceId="21m00Tcm4TlvDq8ikWAM"

export Mistral__ApiKey="<your-key>"
```

## Run

```bash
dotnet run --project samples/Voxa.Samples.AspNetServer
```

Visit `http://localhost:5000/` for the route list, then connect a WebSocket to any of the `/voice/...` paths and stream PCM.

## Wire protocol

Per [`Voxa.Transports.WebSocket.Protocol.WireProtocol`](../../src/Voxa.Transports.WebSocket/Protocol/WireProtocol.cs):

**Client → Server:**
- Binary: 16-bit PCM, sample rate per scenario (24 kHz for Voice Live, 16 kHz default for granular STT)
- Text JSON: `{"type":"hello",...}`, `{"type":"end"}`, `{"type":"toolResult",...}`, `{"type":"text",...}`

**Server → Client:**
- Binary: 16-bit PCM (response audio, vendor-specific output rate)
- Text JSON: `{"type":"transcription",...}`, `{"type":"text",...}`, `{"type":"toolCall",...}`, `{"type":"speaking",...}`, `{"type":"interruption"}`, `{"type":"error",...}`, `{"type":"end"}`

## Quick browser smoke test

Open the dev tools console and run:

```js
const ws = new WebSocket('ws://localhost:5000/voice/azure');
ws.onmessage = e => console.log(typeof e.data === 'string' ? JSON.parse(e.data) : `${e.data.byteLength} bytes audio`);
```

Then capture mic via `getUserMedia` + `AudioContext` and feed PCM to `ws.send(buffer)`.

## Mix-and-match recipe

The whole point of the multi-vendor split: **each STT engine works with each TTS engine, and dropping in `MicrosoftAgentsProcessor` slots a real LLM into the middle.** That's 4 STT × N TTS × any MAF agent = a lot of ground covered by a few small composable processors.
