# Voxa.AspNetCore

Fluent ASP.NET Core integration for [Voxa](https://github.com/michaeljosiah/voxa). Drop a real-time voice pipeline into a MAF-using app with one fluent expression — no manual `Pipeline.Build()` / `PipelineRunner` plumbing, no per-connection boilerplate.

```csharp
app.MapVoxaVoice("/voice", voice => voice
    .UseSpeechToText(() => OpenAISpeech.StreamingTranscription(opts))
    .UseTranscriptionFilter()
    .UseMicrosoftAgent(agent)
    .UseSentenceAggregator()
    .UseTextToSpeech(() => OpenAISpeech.Synthesis(opts)));
```

That's the whole endpoint. WebSocket lifecycle, source/sink wiring, deadlock-safe agent loop, frontend-tool round-trips, and turn-boundary frames are all wired for you.

## What `MapVoxaVoice` does

- Maps a `GET` route that upgrades to a WebSocket on request.
- Accepts the socket and (if configured) reads a typed `hello` envelope before the pipeline starts.
- Builds a per-connection pipeline by invoking each registered processor factory:
  `WebSocketAudioSource → registered processors (in order) → WebSocketAudioSink`.
- Runs a `PipelineRunner` for the connection's lifetime; tears down cleanly when the client disconnects or `EndFrame` is observed.

## Builder surface

```csharp
voice.UseProcessor(ctx => new MyProcessor(ctx.RequestServices.GetRequiredService<...>()))
voice.UseProcessor(() => new MyStatelessProcessor())

// Speech vendor convenience
voice.UseSpeechToText(() => OpenAISpeech.StreamingTranscription(opts))
voice.UseTextToSpeech(() => OpenAISpeech.Synthesis(opts))
voice.UseSentenceAggregator()
voice.UseTranscriptionFilter()
voice.UseSilenceGate()

// Microsoft Agent Framework
voice.UseMicrosoftAgent(agent)
voice.UseMicrosoftAgent(agent, options => { /* configure */ })
voice.UseMicrosoftAgent(ctx => agentFactory(ctx), (ctx, options) => { /* configure */ })

// Endpoint metadata
voice.RequireAuthorization("MyPolicy")
voice.RequireCors("MyCorsPolicy")

// Hello envelope (typed; lands in HttpContext.Items[VoiceHello.HelloMetadataKey])
voice.UseWebSocketHello<MyHello>((ws, ct) => ParseAsync(ws, ct))

// Custom frame types — emit your own JSON envelope without subclassing the sink
voice.UseCustomFrameSerializer(frame =>
    frame is MyFrame f ? JsonSerializer.Serialize(new { type = "myFrame", ... }) : null)
```

## Advanced MAF integration

`UseMicrosoftAgent(agent, options => {...})` exposes the full `MicrosoftAgentVoiceOptions` surface for hosts that need persisted history, frontend tools, post-turn audit, sanitized backend-tool progress, etc.:

```csharp
voice.UseMicrosoftAgent(agent, options =>
{
    options.BuildMessages = (turn, ct) => LoadHistoryAsync(turn.UserText, ct);
    options.IsFrontendTool = name => myFrontendCatalog.Contains(name);
    options.BuildBackendToolStatus = name => name switch
    {
        "pf_get_spending_summary" => "Checking your spending...",
        _ => null,
    };
    options.OnTurnCompleted = (turn, summary, ct) => RecordAuditAsync(turn, summary, ct);
});
```

See the main repo for the full options reference and the underlying `AgentLoopProcessor` design.
