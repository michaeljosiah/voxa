# Voxa.AspNetCore

ASP.NET Core integration for [Voxa](https://github.com/michaeljosiah/voxa). Provides `AddVoxa` service registration and `MapVoxaVoice` endpoint mapping. The `Voxa` meta-package references this and all built-in speech providers together — start there if you want zero boilerplate.

## Zero-config setup (via the `Voxa` meta-package)

```csharp
// Program.cs
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddVoxa(builder.Configuration);  // meta-package 2-arg entry point
var app = builder.Build();
app.UseWebSockets();
app.MapVoxaVoice("/voice").UseDefaults();
app.Run();
```

Configure via `appsettings.json`:

```json
{
  "Voxa": {
    "Profile": "LowLatency",
    "Stt": "OpenAI",
    "Tts": "OpenAI",
    "OpenAI": { "ApiKey": "sk-..." },
    "Agent": {
      "Provider": "OpenAI",
      "Model": "gpt-4o-mini",
      "Instructions": "You are a friendly voice assistant. Keep responses brief."
    }
  }
}
```

`UseDefaults()` composes: **Silero VAD → STT → transcription filter → agent → sentence aggregator → TTS**, with per-connection bounded conversation memory. A `session` envelope is pushed to the client at connection start, announcing the input/output sample rates so the client can configure its encoder/decoder without hardcoding.

**Startup validation:** `VoxaDefaultsGuard` is an `IHostedService` that fires at host startup (only when `UseDefaults()` was called). It verifies `Voxa:Stt`, `Voxa:Tts`, and that an agent is usable — and throws `InvalidOperationException` with a clear message listing the registered providers and what to set if anything is missing. When the agent will come from an `IVoiceAgentFactory` (rather than a DI-registered `AIAgent`/`IChatClient`), the guard calls the factory's `Validate(VoxaAgentOptions)` so an unsupported `Voxa:Agent:Provider` or a missing API key fails at startup, not on the first WebSocket request. Custom factory implementations can override `Validate` to participate; the default implementation reports no errors.

**Sample-rate overrides:** providers accept `Voxa:<Section>:InputSampleRate` / `Voxa:<Section>:OutputSampleRate` overrides (e.g. `Voxa:OpenAI:OutputSampleRate`). The session envelope and the VAD always use the *effective* rate — the override when present, the descriptor default otherwise — so clients and processors never disagree about the audio format.

## À-la-carte setup (using `Voxa.AspNetCore` directly)

Install only the provider packages you want and register them explicitly:

```csharp
builder.Services.AddVoxa(builder.Configuration, voxa => {
    voxa.AddProvider(OpenAISpeechDescriptors.Stt);
    voxa.AddProvider(ElevenLabsDescriptors.Tts);
    voxa.AddProvider(SileroVadDescriptors.Vad);
});
```

The `configure` callback is required (not optional) — this prevents overload-resolution ambiguity with the meta-package's 2-arg overload. `AddVoxa` is idempotent: a second call merges its descriptors into the existing registry without re-registering infrastructure.

## Route building

`MapVoxaVoice(pattern)` returns a `VoxaVoiceRoute` fluent builder:

```csharp
// Fully managed pipeline
app.MapVoxaVoice("/voice").UseDefaults();

// Fully custom pipeline (2-arg overload; no HttpContext access needed)
app.MapVoxaVoice("/voice", pipeline => pipeline
    .UseSpeechToText(() => OpenAISpeech.StreamingTranscription(opts))
    .UseTranscriptionFilter()
    .UseMicrosoftAgent(myAgent)
    .UseSentenceAggregator()
    .UseTextToSpeech(() => OpenAISpeech.Synthesis(opts)));

// Fully custom pipeline with per-request context access (VoxaVoiceRoute.Use)
app.MapVoxaVoice("/voice").Use((ctx, pipeline) => pipeline
    .UseProcessor(ctx => new MyContextAwareProcessor(ctx.User)));

// Defaults + custom extension
app.MapVoxaVoice("/voice")
   .UseDefaults()
   .Use((ctx, pipeline) => pipeline.UseProcessor(() => new MyAuditProcessor()));

// Authorization
app.MapVoxaVoice("/voice").UseDefaults().RequireAuthorization("MyPolicy");
```

`Use()` is composable via `+=` — multiple calls append rather than replace. An unmapped route (neither `UseDefaults()` nor `Use()` called) throws `InvalidOperationException` at request time rather than silently echoing audio.

## Pipeline builder surface

```csharp
pipeline.UseProcessor(ctx => new MyProcessor(ctx.RequestServices.GetRequiredService<...>()))
pipeline.UseProcessor(() => new MyStatelessProcessor())

// Speech vendor convenience
pipeline.UseSpeechToText(() => OpenAISpeech.StreamingTranscription(opts))
pipeline.UseTextToSpeech(() => OpenAISpeech.Synthesis(opts))
pipeline.UseSentenceAggregator()
pipeline.UseTranscriptionFilter()
pipeline.UseSilenceGate()

// Microsoft Agent Framework
pipeline.UseMicrosoftAgent(agent)
pipeline.UseMicrosoftAgent(agent, options => { /* configure */ })
pipeline.UseMicrosoftAgent(ctx => agentFactory(ctx), (ctx, options) => { /* configure */ })

// Endpoint metadata
route.RequireAuthorization("MyPolicy")
route.RequireCors("MyCorsPolicy")

// Hello envelope (typed; lands in HttpContext.Items[VoiceHello.HelloMetadataKey])
route.UseWebSocketHello<MyHello>((ws, ct) => ParseAsync(ws, ct))

// Custom frame types — emit your own JSON envelope without subclassing the sink
pipeline.UseCustomFrameSerializer(frame =>
    frame is MyFrame f ? JsonSerializer.Serialize(new { type = "myFrame", ... }) : null)
```

## Advanced MAF integration

`UseMicrosoftAgent(agent, options => {...})` exposes the full `MicrosoftAgentVoiceOptions` surface for hosts that need persisted history, frontend tools, post-turn audit, sanitized backend-tool progress, etc.:

```csharp
pipeline.UseMicrosoftAgent(agent, options =>
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

## What happens per connection

1. Pipeline is built (from `UseDefaults()` and/or `Use()` callbacks) **before** `AcceptWebSocketAsync` — config errors surface as HTTP 500, not a connected-then-aborted socket.
2. WebSocket is accepted.
3. `PipelineRunner` is started.
4. `SessionInfoFrame` is injected — clients receive `{"type":"session","v":1,"inputSampleRate":16000,"outputSampleRate":24000}`.
5. The runner drives the pipeline for the connection's lifetime; tears down cleanly on client disconnect or `EndFrame`.

See the main repo for the full options reference and the underlying `AgentLoopProcessor` design.
