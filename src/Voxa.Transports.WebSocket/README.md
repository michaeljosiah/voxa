# Voxa.Transports.WebSocket

WebSocket source and sink processors for [Voxa](https://github.com/michaeljosiah/voxa). Operates over any `System.Net.WebSockets.WebSocket` — works with ASP.NET Core, `ClientWebSocket`, or any custom WebSocket subclass.

> **Tip:** for ASP.NET Core hosts, prefer the fluent surface in [`Voxa.AspNetCore`](../Voxa.AspNetCore) — `app.MapVoxaVoice("/voice", voice => voice.UseSpeechToText(...).UseMicrosoftAgent(agent).UseTextToSpeech(...))` wires source, sink, and the agent loop for you. The lower-level API documented below is for hosts that want to build pipelines by hand.

## Install

```bash
dotnet add package Voxa.Transports.WebSocket --prerelease
```

## Quickstart (ASP.NET Core, lower-level)

```csharp
app.Map("/voice", async context =>
{
    using var ws = await context.WebSockets.AcceptWebSocketAsync();

    var pipeline = Pipeline.Build()
        .Source(new WebSocketAudioSource(ws))
        .Then(new AzureVoiceLiveProcessor(opts))
        .Sink(new WebSocketAudioSink(ws));

    await using var runner = new PipelineRunner(pipeline, context.RequestAborted);
    await runner.StartAsync(ct: context.RequestAborted);
    await runner.WaitAsync().WaitAsync(context.RequestAborted);
});
```

## Wire protocol

Binary WebSocket frames carry **raw 16-bit PCM** (sample rate per `WebSocketAudioOptions`). Text WebSocket frames carry **typed JSON envelopes** via `WireProtocol`:

**Client → Server:** `hello`, `end`, `text`, `toolResult`
**Server → Client:** `transcription`, `text`, `toolCall`, `speaking`, `interruption`, `status`, `error`, `end`

The `status` envelope carries sanitized progress hints for client UI (e.g. *"Checking your spending..."*). Voxa's `MicrosoftAgentVoice` driver emits these via `StatusFrame` whenever a host has configured `MicrosoftAgentVoiceOptions.BuildBackendToolStatus` for a backend tool.

## Custom envelopes — host-defined frames

`WebSocketAudioSink` accepts a `customSerializer` hook so hosts can ship their own JSON envelopes for frame types Voxa doesn't know about — without subclassing the sink and re-implementing the send discipline. Return a non-null string to emit it as a text frame; return `null` to fall through to Voxa's built-in switch.

```csharp
var sink = new WebSocketAudioSink(ws, customSerializer: frame =>
    frame is MyHostFrame f
        ? JsonSerializer.Serialize(new { type = "myEnvelope", value = f.Value })
        : null);
```

Caller owns the `WebSocket` lifetime; the processors will not dispose it.

## License

MIT.
