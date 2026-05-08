# Voxa.Services.AzureVoiceLive

Composite STT+LLM+TTS+VAD processor for [Voxa](https://github.com/michaeljosiah/voxa), backed by the Azure Voice Live API.

The same processor speaks **Azure Voice Live**, **Azure OpenAI Realtime**, and **OpenAI Realtime** — they share a wire protocol, so only the endpoint URL and auth header change.

## Install

```bash
dotnet add package Voxa.Services.AzureVoiceLive --prerelease
```

## Quickstart

```csharp
var options = new AzureVoiceLiveOptions
{
    Endpoint = new Uri("wss://<resource>.cognitiveservices.azure.com/voice-live/realtime?model=gpt-realtime-mini&api-version=2025-10-01"),
    ApiKey = "<your-key>",
    Model = "gpt-realtime-mini",
    Voice = "alloy",
    Instructions = "You are a friendly voice assistant.",
};

var pipeline = Pipeline.Build()
    .Source(new WebSocketAudioSource(ws))
    .Then(new AzureVoiceLiveProcessor(options))
    .Sink(new WebSocketAudioSink(ws));
```

## What's included

- `AzureVoiceLiveProcessor` — opens a WebSocket, sends `session.update`, streams `AudioRawFrame` up as `input_audio_buffer.append`, decodes server events into `Transcription`/`Audio`/`ToolCall`/`Speaking`/`Interruption`/`Error` frames.
- `IRealtimeApiTransport` + `WebSocketRealtimeApiTransport` — wire-level transport, swappable for tests.
- `RealtimeEventCodec` — outbound JSON builders + inbound event decoder.
- `AzureVoiceLiveOptions`, `AzureVoiceLiveTurnDetection`, `AzureVoiceLiveTool`.

Tool calls are emitted as `ToolCallRequestFrame`s; feed results back via `ToolCallResultFrame`.

## License

MIT.
