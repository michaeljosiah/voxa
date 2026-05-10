# Voxa.Services.MicrosoftAgents

Adapter that wraps any [Microsoft Agent Framework](https://learn.microsoft.com/en-us/agent-framework) `AIAgent` as a [Voxa](https://github.com/michaeljosiah/voxa) `FrameProcessor`.

## Install

```bash
dotnet add package Voxa.Services.MicrosoftAgents --prerelease
```

## Quickstart

```csharp
using Microsoft.Agents.AI;
using Voxa.Services.MicrosoftAgents;

var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    Name = "my-agent",
});

var pipeline = Pipeline.Build()
    .Source(new WebSocketAudioSource(ws))
    .Then(OpenAISpeech.StreamingTranscription(speechOpts))
    .Then(MicrosoftAgentVoice.CreateProcessor(agent))   // ← here
    .Then(new SentenceAggregator())
    .Then(OpenAISpeech.Synthesis(speechOpts))
    .Sink(new WebSocketAudioSink(ws));
```

`MicrosoftAgentVoice.CreateProcessor` returns a fully-configured `Voxa.Core.AgentLoopProcessor` — Voxa owns the deadlock-safe data-loop / turn-worker split, frontend-tool TCS correlation, agent re-invocation loop, turn-boundary frames, and token aggregation.

## Configure with delegates

Hosts that need persisted history, frontend tools, post-turn audit, or sanitized backend-tool progress configure those via `MicrosoftAgentVoiceOptions`:

```csharp
MicrosoftAgentVoice.CreateProcessor(agent, options =>
{
    // Build the message list for this turn (load history, prepend a user-context preamble, …).
    options.BuildMessages = (turn, ct) => LoadHistoryAsync(turn.UserText, ct);

    // Per-turn run options (frontend-tool declarations, model override, telemetry).
    options.BuildRunOptions = turn => myRunOptions;

    // Classify a tool name as frontend (round-trips to client) vs backend (MAF auto-executes).
    options.IsFrontendTool = name => myFrontendCatalog.Contains(name);

    // Surface a sanitized progress message while a backend tool runs ("Checking your spending...").
    // Return null to suppress for a given tool. NEVER include raw tool names — this is user-facing.
    options.BuildBackendToolStatus = name => name switch
    {
        "pf_get_spending_summary" => "Checking your spending...",
        _ => null,
    };

    // Lifecycle hooks fired by the surrounding AgentLoopProcessor.
    options.OnTurnStarted   = (turn, ct) => MetricsAsync(turn, ct);
    options.OnTurnCompleted = (turn, summary, ct) => AuditAsync(turn, summary, ct);
    options.OnTurnFailed    = (turn, ex, ct) => LogFailureAsync(turn, ex, ct);
});
```

`TurnSummary.Usage` carries the per-turn input/output token totals aggregated from `UsageContent` updates the model emits — wire it into your audit row from `OnTurnCompleted`.

## Frontend vs backend tools

- **Frontend tools** — round-trip through the pipeline. The driver yields a `ToolCallRequestFrame`, the host sends it to the client (the WebSocket transport ships a `toolCall` envelope), the client returns a `ToolCallResultFrame`, and the driver re-invokes the agent with the result appended.
- **Backend tools** — MAF auto-executes inline. Voxa does not yield a frame for the raw `FunctionCallContent` (raw tool names like `pf_*` must not leak to consumer UI). Hosts opt into a sanitized client-facing progress envelope by returning a non-null string from `BuildBackendToolStatus`; the driver then yields a `StatusFrame` which the WebSocket transport ships as `{ "type": "status", "message": "..." }`.

## Backend tool progress pattern

The natural conversational flow ("acknowledge → run a tool → speak the result") is supported out of the box:

1. The model streams an acknowledgement as `TextContent`. Voxa yields it as `LlmTextChunkFrame` immediately, so `SentenceAggregator` flushes the sentence and TTS starts speaking *before* the backend tool runs.
2. The model emits a backend `FunctionCallContent`. MAF auto-executes the tool synchronously inside `RunStreamingAsync`.
3. While the tool runs, Voxa optionally emits `StatusFrame("Checking your spending...")` for the client UI.
4. The model resumes streaming the final answer text. Frontend display tools (`display_pie_chart`, etc.) round-trip through the pipeline as normal.

Targets `Microsoft.Agents.AI` 1.5.0.

## License

MIT.
