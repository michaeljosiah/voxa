# Getting started

Voxa turns a microphone and an LLM into a real-time voice agent. The fastest path is the `Voxa`
meta-package plus `UseDefaults()` — a working voice endpoint in about five lines, no knowledge of
frames required.

## Five lines to a voice bot

```csharp
// Program.cs
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddVoxa(builder.Configuration);   // meta-package: registry + every built-in provider
var app = builder.Build();
app.UseWebSockets();
app.MapVoxaVoice("/voice").UseDefaults();
app.Run();
```

Configure it from `appsettings.json` under the `"Voxa"` section:

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

`UseDefaults()` composes the standard chain — **VAD → STT → transcription filter → agent → sentence
aggregator → TTS** — from the registered providers and the active latency profile, and announces the
session sample rates to the client at connection start. `AddVoxa` adds a fail-fast validator
(`VoxaDefaultsGuard`), so the host refuses to start on an unknown provider/profile or a missing agent
rather than failing on the first call.

## Supplying the agent

`UseDefaults()` needs an agent. It resolves one in this order:

1. an **`AIAgent`** registered in DI, else
2. an **`IChatClient`** registered in DI (wrapped into a `ChatClientAgent`), else
3. **`Voxa:Agent:Provider`** (requires the `Voxa` meta-package, which ships provider-backed factories).

Register your own when you want full control over the model, tools, or system prompt:

```csharp
builder.Services.AddSingleton<AIAgent>(/* your Microsoft Agent Framework agent */);
```

## Latency profiles

Named profiles bundle the tuning from [`performance-tuning.md`](performance-tuning.md) so you don't
have to learn the individual knobs:

| Profile       | Use it for                                             |
| ------------- | ------------------------------------------------------ |
| `LowLatency`  | Snappiest turn-taking; eager first TTS chunk, tight VAD |
| `Quality`     | Accuracy/completeness over latency                     |
| `Cheap`       | Same robustness caps as LowLatency, relaxed timings    |
| `Default`     | Balanced; every robustness knob off                    |

Set it with `Voxa:Profile`. Individual knobs (`Voxa:Vad:*`, `Voxa:Aggregator:*`, `Voxa:Agent:*`)
override the profile when present.

## Custom conversation memory under the defaults (VDX-006)

By default `UseDefaults()` keeps a per-connection, in-memory chat history. If your app has its **own**
durable store — you want to persist turns, key them by a thread id, or show voice turns next to text —
register an `IVoiceAgentConfigurator`. You keep the composer (the real VAD, the latency profile, the
diagnostics taps); only the agent's per-turn options change, and the built-in in-memory history steps
aside so your store owns memory:

```csharp
public sealed class MyVoiceMemory(IConversationStore store) : IVoiceAgentConfigurator
{
    public void Configure(IServiceProvider connection, MicrosoftAgentVoiceOptions options)
    {
        // connection is the per-connection scope (HTTP RequestServices) — resolve scoped state
        // such as the active thread id from it here.
        options.BuildMessages = (turn, ct) =>
        {
            var messages = store.LoadRecent();                       // your history
            messages.Add(new ChatMessage(ChatRole.User, turn.UserText));
            return ValueTask.FromResult<IReadOnlyList<ChatMessage>>(messages);
        };
        options.OnTurnCompleted = (turn, summary, ct) =>
        {
            store.Append(turn.UserText, summary.AssistantText);      // persist the finished turn
            return ValueTask.CompletedTask;
        };
    }
}

builder.Services.AddSingleton<IVoiceAgentConfigurator, MyVoiceMemory>();
app.MapVoxaVoice("/voice").UseDefaults();   // unchanged: still VAD + profile + diagnostics
```

Prefer a lambda? Use the convenience registration:

```csharp
builder.Services.AddVoxaVoiceAgentConfigurator((connection, options) =>
{
    options.OnTurnCompleted = (turn, summary, ct) => { /* persist */ return ValueTask.CompletedTask; };
});
```

The same seam reaches frontend-tool routing and the turn-lifecycle hooks (`OnTurnStarted` /
`OnTurnFailed`), since it hands you the full `MicrosoftAgentVoiceOptions`. See
[`specifications/vdx-006-custom-conversation-memory-spec.html`](specifications/vdx-006-custom-conversation-memory-spec.html).

## Going further

- **À-la-carte providers** and **fully custom pipelines** (`MapVoxaVoice(pattern, configure)` /
  `.Use(...)`): see the [`Voxa.AspNetCore` README](../src/Voxa.AspNetCore/README.md).
- **Local, on-device speech** (whisper.cpp / Piper / Kokoro / Silero): [`local-speech.md`](local-speech.md).
- **Tuning latency**: [`performance-tuning.md`](performance-tuning.md).
- **The desktop playground** (live pipeline against your mic): [`studio.md`](studio.md).
