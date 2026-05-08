# Voxa.Samples.AspNetServer

Minimal ASP.NET Core voice-agent server. Composes the full Voxa stack end-to-end:

```
WebSocketAudioSource → AzureVoiceLiveProcessor → WebSocketAudioSink
```

## Configure

Edit `appsettings.json` or set environment variables:

```bash
export AzureVoiceLive__Endpoint="wss://<your-resource>.cognitiveservices.azure.com/voice-live/realtime?model=gpt-realtime-mini&api-version=2025-10-01"
export AzureVoiceLive__ApiKey="<your-key>"
```

## Run

```bash
dotnet run --project samples/Voxa.Samples.AspNetServer
```

The server listens on `http://localhost:5000` (Kestrel default) — visit `/` for instructions, connect a WebSocket to `/voice`.

## Wire protocol

Per [`Voxa.Transports.WebSocket.Protocol.WireProtocol`](../../src/Voxa.Transports.WebSocket/Protocol/WireProtocol.cs):

**Client → Server:**
- Binary: 16-bit PCM @ 24 kHz mono (raw audio)
- Text JSON: `{"type":"hello",...}`, `{"type":"end"}`, `{"type":"toolResult",...}`, `{"type":"text",...}`

**Server → Client:**
- Binary: 16-bit PCM @ 24 kHz mono (response audio)
- Text JSON: `{"type":"transcription",...}`, `{"type":"text",...}`, `{"type":"toolCall",...}`, `{"type":"speaking","who":"bot|user","started":true}`, `{"type":"interruption"}`, `{"type":"error","message":"..."}`, `{"type":"end"}`

## Quick browser test

Open the dev tools console on any localhost-allowed page and run:

```js
const ws = new WebSocket('ws://localhost:5000/voice');
ws.onmessage = (e) => console.log(typeof e.data === 'string' ? JSON.parse(e.data) : `${e.data.byteLength} bytes audio`);
```

Then capture mic via `getUserMedia` + `AudioContext` to feed PCM frames over `ws.send(buffer)`.
