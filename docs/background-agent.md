# Background agent delegation (the talker/thinker split)

> Spec: [VDX-008](specifications/vdx-008-background-agent-spec.html) · Sample:
> [`samples/Voxa.Samples.BackgroundAgentServer`](../samples/Voxa.Samples.BackgroundAgentServer/Program.cs)

In text chat a slow tool call is a spinner; in voice it is dead air. The voice budget is under a
second to first audio, but real agent work — web search, RAG, a multi-step tool chain — takes
5–30 s. VDX-008 splits the agent in two:

- the **interaction model** (the *talker* — `Voxa:Agent:*`, a fast tier) owns the conversation and
  the latency budget;
- a **background agent** (the *thinker* — any `IAgentTurnDriver`, a heavyweight tier) runs tools and
  reasoning off the critical path.

The talker delegates explicitly with a `delegate_task` tool call, keeps talking ("sure — give me a
moment"), and when the result lands it re-enters the conversation as a new turn — *if the talker
decides it's still relevant*. Nothing is mirrored: with no delegation, the pipeline runs exactly one
model.

## Quick start

```csharp
builder.Services.AddVoxa(builder.Configuration);

builder.Services.AddVoxaBackgroundAgent(_ =>
    MicrosoftAgentVoice.CreateTurnDriver(researcherAgent));  // any AIAgent, or your own IAgentTurnDriver

app.MapVoxaVoice("/voice").UseDefaults();
```

Registering the driver is the whole opt-in. The composer then:

1. inserts a `BackgroundAgentProcessor` right after the agent stage,
2. gives the interaction model a `delegate_task(goal, context_summary)` tool, and
3. arms the agent loop's hold/release arbitration (below).

Unregistered, the composed pipeline is **byte-identical** to one built before VDX-008 existed
(golden-tested).

`AddVoxaBackgroundAgent` registers the driver **scoped** — one instance per connection, because
background drivers typically hold per-conversation state. A stateless, thread-safe driver may be
registered manually: `services.AddKeyedSingleton<IAgentTurnDriver>(VoxaBackgroundAgentOptions.ServiceKey, …)`.

## How a delegation flows

```
user: "find me a flight to Lisbon in March"
  └─ talker calls delegate_task(goal, context_summary)
       ├─ tool returns instantly: "Delegated (task …). Acknowledge — do NOT invent the answer."
       ├─ talker speaks: "On it — anything else meanwhile?"          ← conversation never stalls
       └─ BackgroundTaskRequestFrame → BackgroundAgentProcessor
            └─ background driver runs (tools, browsing, reasoning; seconds later…)
                 └─ BackgroundTaskCompletedFrame → agent loop
                      └─ a NEW turn: the talker reads the result + relevance gate
                           ├─ still relevant → speaks it
                           └─ conversation moved on → yields nothing (silence is a valid outcome)
```

Progress is visible along the way: `StatusFrame`s the background driver yields are forwarded to the
client ("Searching flights…") — the same sanitized-status channel backend tools already use.

## Arbitration: results never talk over the user

A result can land at any moment, including mid-utterance. The agent loop holds it and releases
**data-ordered**: behind the turn triggered by the utterance's final transcription — never on the
stop-speaking edge (which the STT stage emits *before* the transcript), and never while the user is
talking. A quiet-timeout covers utterances whose final never arrives. Held results beyond the cap
drop oldest; over-cap delegation *requests* are instead **rejected** with an immediate error result,
because the talker verbally promised that work and must be able to say so.

## Configuration (`Voxa:BackgroundAgent:*`)

| Key | Default | Meaning |
|---|---|---|
| `MaxConcurrentTasks` | 2 | Background worker pool per session. |
| `MaxQueuedRequests` | 8 | Waiting-request cap; excess rejected with an `IsError` completion. |
| `TaskTimeoutSeconds` | 120 | Per-task wall-clock cap; timeout completes as an error, not silence. |
| `MaxPendingResults` | 4 | Held-result cap while the user speaks (drop-oldest). |
| `HoldWhileUserSpeaking` | true | The arbitration described above; off ⇒ results enqueue immediately. |
| `HeldResultReleaseTimeoutMs` | 2000 | Fallback release when an utterance's final never arrives. |

All knobs are range-validated at startup (fail-fast, like every other `Voxa:*` section).

## The prompt contract

Most of the quality lives in prompts, not plumbing:

1. **After delegating, acknowledge — never fabricate.** The `delegate_task` ack string carries this
   instruction, and the sample's talker instructions reinforce it.
2. **On a result turn, decide relevance first.** The result reaches the talker framed by a
   relevance-gate instruction (`MicrosoftAgentVoice.CreateBackgroundResultMessage`): if the
   conversation has moved on, respond with nothing.
3. **Keep goals, context summaries, and results compact.** They all cross latency-bounded voice
   turns; token bloat is spoken-latency bloat. The background driver's instructions should demand a
   2–3 sentence answer with no markdown (it will be read aloud).

Model tiers: the split only pays off if the talker is actually fast — put `Voxa:Agent` on a
fast/cheap tier and the background agent on the heavyweight tier.

## Hand-rolled hosts (VDX-007 drivers, custom pipelines)

A host that drives its own `IAgentTurnDriver` (the VDX-007 seam) participates directly:

- **Delegate** by emitting `BackgroundTaskRequestFrame(taskId, goal, contextJson, originTurnId)` via
  `ctx.Emitter` from its tool dispatch.
- **Consume results** by checking `ctx.Trigger == TurnTrigger.BackgroundResult` and reading
  `ctx.BackgroundResult` (for `UserUtterance` turns it is null). Yield nothing to gate a stale
  result to silence.
- Overriding `MicrosoftAgentVoiceOptions.BuildMessages` takes on the same trigger check —
  `CreateBackgroundResultMessage` keeps the contract.

Hand-built (non-composer) pipelines place `new BackgroundAgentProcessor(backgroundDriver, …)`
anywhere downstream of the `AgentLoopProcessor`.

## Semantics worth knowing

- **Barge-in does not cancel background work** — the user interrupting the talker must not orphan a
  promised task. `EndFrame`/session teardown does cancel it (bounded join; tasks die with the session).
- **Background output is contained by whitelist.** Text the background driver produces
  (`LlmTextChunkFrame`, `TextFrame`) accumulates into the result and is never spoken directly;
  `StatusFrame` passes for progress UI; `LlmUsageFrame` aggregates into the completion's token
  totals; anything else is dropped. Frontend tools throw in background turns — nobody is looking at
  a modal for delegated work.
- **A failed or timed-out task completes with `IsError`** and flows to the talker like any result,
  so it can apologize and offer to retry; the session never degrades.
- **Diagnostics** (when `Voxa:Diagnostics:Enabled`): `BackgroundTask{Started,Completed,Rejected,Dropped}`
  hub events, turn edges tagged with their trigger kind (so gated-to-silence turns don't read as
  zero-output anomalies), and a `voxa.background.task.duration` histogram on the `Voxa` meter.

## Limitations (v1)

- Granular pipelines only — the composite processors (`AzureVoiceLiveProcessor`,
  `OpenAIRealtimeProcessor`, speech-to-speech) own their conversation loop vendor-side.
- One background driver per session; fan-out/specialist routing belongs *inside* that driver.
- Tasks don't survive the session — durable jobs are a host concern.
