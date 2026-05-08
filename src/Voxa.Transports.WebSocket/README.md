# Voxa.Transports.WebSocket

WebSocket source and sink processors for [Voxa](https://github.com/michaeljosiah/voxa). Operates over any `System.Net.WebSockets.WebSocket` — works with ASP.NET Core, `ClientWebSocket`, or any custom WebSocket subclass.

## Install

```bash
dotnet add package Voxa.Transports.WebSocket --prerelease
```

## Quickstart (ASP.NET Core)

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
**Server → Client:** `transcription`, `text`, `toolCall`, `speaking`, `interruption`, `error`, `end`

Caller owns the `WebSocket` lifetime; the processors will not dispose it.

## License

MIT.
