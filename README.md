<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/voxa-logo-animated.svg">
    <img src="assets/voxa-logo-animated-light.svg" alt="VOXA" width="300">
  </picture>
</p>

<p align="center">
  <a href="https://github.com/michaeljosiah/voxa/actions/workflows/ci.yml"><img src="https://github.com/michaeljosiah/voxa/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-blue.svg" alt="License: MIT"></a>
  <a href="https://dotnet.microsoft.com/"><img src="https://img.shields.io/badge/.NET-10.0-512BD4" alt=".NET 10"></a>
</p>

**Voxa** is a frame-based, real-time voice AI pipeline framework for **.NET 10** — native STT, LLM, and TTS composed into low-latency voice agents. Inspired by [Pipecat](https://github.com/pipecat-ai/pipecat); built around the Microsoft Agent Framework, Azure Voice Live, and Azure Speech.

> See [`ROADMAP.md`](ROADMAP.md) for tracked work — next up is the turn-taking program (backchannel-aware barge-in gating, a local end-of-turn model, behavioral evals, pipeline health watchdogs — specs VRT-006/VLS-010/VDX-009/VRT-007), plus session resilience and AONIK integration.

> **Status: pre-alpha.** Public API stabilising. Packages are published on [NuGet](https://www.nuget.org/packages?q=Voxa) as prerelease (`*-alpha`) — pin exact versions and expect breaking changes.

> _Voxa is an independent, non-commercial open-source project for the .NET ecosystem, and is not affiliated with or endorsed by any other product or company using the name "Voxa"._

## What it is

Voxa lets you compose real-time voice agents from small, testable processors. Each processor consumes and emits typed `Frame`s — audio, transcription, tool calls, control signals. System frames (interruption, errors) preempt data frames in their own task. Pipelines run asynchronously with bounded backpressure on data, unbounded priority on system signals.

## Quickstart — five lines

Reference the `Voxa` meta-package (which includes `Voxa.AspNetCore` and all built-in speech providers):

```csharp
// Program.cs
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddVoxa(builder.Configuration);
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

`UseDefaults()` wires VAD → STT → agent → sentence aggregation → TTS for you, with per-connection conversation memory and a `session` frame that announces sample rates to the client.

**Startup validation:** if `Voxa:Stt`, `Voxa:Tts`, or an agent are missing, the host refuses to start with a clear error listing the registered providers and what to set.

### Run it fully local — zero API keys

Swap the providers for the local tier and the same five lines run **without any cloud account**
(whisper.cpp STT + Piper TTS + a built-in echo agent; first run downloads the models, after that
no network is needed at all):

```json
{
  "Voxa": {
    "Stt": "WhisperCpp",
    "Tts": "Piper",
    "Agent": { "Provider": "Echo" }
  }
}
```

Set `"Tts": "Kokoro"` for markedly more natural speech (heavier on CPU), and swap `"Echo"` for a
real agent when you have keys. Details — model catalogs, latency expectations, air-gapped
deployment, zero-cost CI — in [`docs/local-speech.md`](docs/local-speech.md).

### Smart turn detection — don't cut people off mid-thought

A silence-only VAD ends the turn on a fixed pause, so `Voxa:Vad:StopDurationMs` has to stay conservative
(~800 ms) to avoid clipping someone who pauses to think. The **opt-in** `Voxa.Audio.SmartTurn` package
adds a classifier on top of the silence VAD: when silence is detected it asks *"is the user actually
done?"* — and only a **complete** verdict ends the turn, so that timeout can drop to ~200 ms.

```csharp
builder.Services.AddVoxa(builder.Configuration);
builder.Services.AddVoxaSmartTurn(builder.Configuration);   // opt-in; reads Voxa:SmartTurn
```

Point `Voxa:SmartTurn:Provider` at an `Http` model server, or run the real `pipecat-ai/smart-turn-v3`
model in a Voxa-managed local Python `Sidecar`. It's **zero-cost when unregistered** (classic silence VAD,
unchanged) and **fails "complete"** on any classifier error, so a flaky endpoint never strands a turn.
Guide: [`src/Voxa.Audio.SmartTurn/README.md`](src/Voxa.Audio.SmartTurn/README.md).

### Barge-in that actually stops the bot

Talking over the bot cancels the in-flight turn — the agent loop cancels the driver enumeration and
pushes a real `InterruptionFrame`, the sentence aggregator and TTS mute the stale tail, and audio
already queued for the socket is purged (epoch-stamped queue) so the `interruption` envelope jumps
ahead. Enabled by default on the granular chain; opt out programmatically
(`cancelTurnOnBargeIn: false`) for half-duplex hosts. Making the trigger smarter — so an "uh-huh"
backchannel *doesn't* kill the answer — is specced as
[VRT-006](docs/specifications/vrt-006-turn-taking-strategies-spec.html).

### Bring your own agent runtime

`UseDefaults()` doesn't lock you into the built-in agent factory. Register your own
`IAgentTurnDriver` in DI and the composed pipeline uses it as the agent stage — your engine, your
history, your tool loop, while Voxa keeps owning VAD/STT/aggregation/TTS (VDX-007). Pair it with
`IVoiceAgentConfigurator` (VDX-006) to own conversation memory without giving up the five-line
setup. This is how downstream apps (e.g. a desktop assistant with its own engine) ride the default
composition with zero custom pipeline code.

### Telephony — put the pipeline on a phone call

`Voxa.Transports.Twilio` answers Twilio Media Streams over the same WebSocket seam (no WebRTC):
G.711 μ-law codec, 8 kHz↔pipeline resample bridge, `X-Twilio-Signature` validation, and the same
barge-in epoch purge phone callers expect.

```csharp
app.MapVoxaTwilioVoice("/twilio/voice");   // TwiML <Connect><Stream> points here
```

Runnable sample: [`samples/Voxa.Samples.TwilioServer`](samples/Voxa.Samples.TwilioServer). The
vendor-neutral base lives in `Voxa.Transports.Telephony` for other carriers.

### Background agent delegation — no dead air on slow tools

In voice, a 10-second tool call is 10 seconds of silence. VDX-008 splits the agent in two: the
**interaction model** (fast tier) owns the conversation, and a **background agent** (heavyweight
tier) runs tools, browsing, and multi-step reasoning off the critical path. The talker delegates
explicitly via a `delegate_task` tool, acknowledges immediately, and when the result lands it
re-enters as a new turn — *gated for relevance* by the talker, which may stay silent if the
conversation has moved on. Results are never injected while the user is speaking.

```csharp
builder.Services.AddVoxa(builder.Configuration);
builder.Services.AddVoxaBackgroundAgent(_ =>
    MicrosoftAgentVoice.CreateTurnDriver(researcherAgent));  // opt-in; any IAgentTurnDriver
```

**Zero-cost when unregistered** — the composed pipeline is byte-identical without it. Barge-in
never cancels delegated work; failed or timed-out tasks come back as apologizable errors, not
silence. Guide: [`docs/background-agent.md`](docs/background-agent.md) · runnable sample:
[`samples/Voxa.Samples.BackgroundAgentServer`](samples/Voxa.Samples.BackgroundAgentServer/Program.cs).

### Voxa Studio — talk to the pipeline and watch it think

A desktop app (Windows) that runs the real pipeline against your mic and speakers and shows
you what's happening inside it. Keyless out of the box — no cloud account needed.

```bash
dotnet run --project apps/Voxa.Studio
```

**The eight views:**

1. **Talk** — pick a microphone and speaker, press **● Start session**, and speak. The first
   session downloads the default models (~155 MB, progress shown); after that it's fully
   offline. You get a streaming transcript, a **live VAD probability trace** (watch the gate
   open as you speak), and a **per-turn latency waterfall** showing exactly where the response
   time went: `VAD → STT → AGENT → TTS → OUT`. Talk over the bot to test barge-in; click any
   waterfall stage to jump to its trend in Metrics.
2. **Playgrounds** — two standalone labs behind one switch. The **STT lab** transcribes the
   bundled `jfk.wav` fixture, any WAV you drop on it, or a live mic recording with any pinned
   Whisper model, stamps each result with its final-transcript latency, and computes a live
   **WER** diff against a reference text (run two models side-by-side for the accuracy/speed
   trade-off). The **TTS lab** synthesizes through the real Piper/Kokoro engines with TTFB and
   RTF measured on your hardware — replayable take history with a waveform scrubber, **A**/**B**
   pins plus an **A/B/X blind test**, a stress-phrase deck, and a batch bench that tables TTFB
   p50/p95 per voice (CSV export).
3. **Voices** — a managed voice library over the live providers. See every voice your pipeline
   can use — local Piper/Kokoro catalogs, the live voices from each keyed cloud provider, and
   the ones you've **cloned** — each tagged Live / Stale / Discovered. **Clone a voice** from a
   few seconds of audio to ElevenLabs or Mistral behind a consent gate (the create button stays
   disabled until you attest you have the right to clone it), then pick it in Config like any
   built-in voice. Local keyless cloning (ONNX) is *coming soon*. Guide:
   [`docs/voice-library.md`](docs/voice-library.md).
4. **Builder** — a node canvas over the live provider registry. Wire Source → VAD → STT →
   agent → TTS → Sink with typed ports (incompatible wires refuse with the reason in words),
   edit options in the inspector, then **Run graph** — the canvas compiles to the same chain a
   server composes and runs it live, with edges pulsing on real frame events and per-stage
   latency on the nodes. Export the result as an `appsettings.json` block, or as generated C#
   composition code when the shape goes beyond what config can express.
5. **Metrics** — turn sessions into evidence. Record a **run** from the mic, a WAV, or a
   **scripted utterance deck** (same input, two configs, honest comparison); get TTFB
   percentiles, per-turn stage stacks, per-stage trends, and a one-sentence takeaway naming
   the dominant stage and the knob to turn. Compare any two runs — with a warning when the
   machine context differs. Bundles are JSON under `~/voxa-runs`; nothing leaves the machine.
6. **Diarization** — "who spoke when" analytics over a recording or a live session: speaker
   timeline, per-speaker talk-time, and the segmentation the `Voxa.Audio.Diarization` pipeline
   produced (pyannote segmentation on the shared ONNX host; the model is a one-click download).
7. **Models** — see what's in the model cache, re-verify hashes, purge entries, or
   **Prefetch full catalog** and copy the folder to provision an air-gapped machine.
8. **Config** — compose a pipeline from dropdowns (fed by the live provider registry, filtered
   to the providers you've activated in **Settings**) and export the `appsettings.json` block for
   your server — or open the draft as a graph in the Builder. **To talk to a real LLM instead of
   the echo agent:** add `OpenAI` in Settings with your key, set *Agent* to `OpenAI`, enter a chat
   model (e.g. `gpt-4o-mini`), then press **⚡ Apply to Studio** — the next Talk session answers
   with the model. Keys are applied to the running app only; they are never written into the export.
   A **Smart turn detection** toggle here wires the opt-in classifier (local Python sidecar or an HTTP
   model server) into the pipeline; **Models** and **Voices** add per-provider filters to narrow long lists.

**Settings** (the gear at the foot of the nav rail) — manage which providers are active and store
their API keys. Add a provider from a card-grid picker (OpenAI, Azure, ElevenLabs, Mistral), enter
its key once, and it is encrypted to disk (Windows DPAPI, scoped to your user account) and live from
the next launch — no environment variables, no re-typing. Activating one identity can light up
several roles at once: `OpenAI` covers STT, TTS *and* the chat agent off a single key. Local
providers (Whisper, Piper, Kokoro, Echo) are always listed and need no keys. Activated providers are
exactly the ones Config offers. Keys never leave the machine and are never written into any export.
Guide: [`docs/settings.md`](docs/settings.md).

**Pipeline profiles** — save a pipeline you've composed (in Config or the Builder) as a named profile,
then switch the whole app to it from the **Pipeline Profile** bar above every view: Talk, the Playgrounds,
the lot, all at once. The choice persists, so Studio reopens on the pipeline you left. Profiles store only
the provider/model selection — **never API keys** (those stay in the encrypted secrets layer).

Full guide — every view, server-side diagnostics, troubleshooting: [`docs/studio.md`](docs/studio.md).

## À-la-carte configuration

For hosts that install only specific provider packages or need custom pipeline composition:

```csharp
// Register only the providers you have installed
builder.Services.AddVoxa(builder.Configuration, voxa => {
    voxa.AddProvider(OpenAISpeechDescriptors.Stt);
    voxa.AddProvider(ElevenLabsDescriptors.Tts);
    voxa.AddProvider(SileroVadDescriptors.Vad);
});

// Compose the pipeline yourself (pipeline is a VoicePipelineBuilder)
app.MapVoxaVoice("/voice", pipeline => pipeline
    .UseSpeechToText(() => OpenAISpeech.StreamingTranscription(opts))
    .UseTranscriptionFilter()
    .UseMicrosoftAgent(myAgent)
    .UseSentenceAggregator()
    .UseTextToSpeech(() => OpenAISpeech.Synthesis(opts)));
```

Or mix the two — call `UseDefaults()` first, then append processors with `Use()`:

```csharp
app.MapVoxaVoice("/voice")
   .UseDefaults()
   .Use((ctx, pipeline) => pipeline.UseProcessor(() => new MyAuditProcessor()));
```

The lower-level API remains available for hosts that want to build the pipeline entirely by hand:

```csharp
var pipeline = Pipeline.Build()
    .Source(new WebSocketAudioSource(ws))
    .Then(new AzureVoiceLiveProcessor(opts))
    .Sink(new WebSocketAudioSink(ws));

await using var runner = new PipelineRunner(pipeline);
await runner.StartAsync();
await runner.WaitAsync();
```

## Configuration reference

All keys live under the `Voxa` section. Provider sub-sections (e.g. `Voxa:OpenAI`, `Voxa:ElevenLabs`) are bound by each provider's descriptor — adding a provider never requires touching `VoxaOptions`.

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `Voxa:Profile` | string | `"Default"` | Named latency preset. `Default`, `LowLatency`, `Quality`, or `Cheap`. |
| `Voxa:Stt` | string | — | STT provider name: cloud `"OpenAI"` / `"Azure"` / `"Deepgram"` / `"AssemblyAI"` / `"Gladia"` / `"Speechmatics"` / `"Google"` / `"Aws"` / `"Groq"` / `"Together"`, or local `"WhisperCpp"` (no key) / `"Voxtral"` (GPU). Required when using `UseDefaults()`. |
| `Voxa:Tts` | string | — | TTS provider name: `"OpenAI"`, `"ElevenLabs"`, `"Azure"`, `"Mistral"`, `"Sidecar"`, or local/no-key `"Piper"` / `"Kokoro"`. Required when using `UseDefaults()`. |
| `Voxa:Vad:Engine` | string | `"Silero"` | `"Silero"`, `"SilenceGate"` (energy-only), or `"None"`. |
| `Voxa:Vad:StopDurationMs` | int | ~800 | Silence that ends the user's turn. Safe to drop to ~200 with a smart-turn classifier registered. |
| `Voxa:SmartTurn:Provider` | string | — | Opt-in smart-turn classifier (needs `AddVoxaSmartTurn` + the `Silero` VAD): `"Http"`, `"Sidecar"`, or `"None"`. Absent/`None` = classic silence-only VAD. |
| `Voxa:SmartTurn:Endpoint` | string | — | Model-server URL for the `Http` classifier (the `Sidecar` provider uses `PythonScript`/`ExecutablePath` instead). |
| `Voxa:Agent:Provider` | string | — | `"OpenAI"` uses the built-in factory; `"Echo"` is a keyless diagnostic agent for demos/CI. Omit to supply your own `AIAgent` / `IChatClient` via DI. |
| `Voxa:Agent:Model` | string | `"gpt-4o-mini"` | Chat model passed to the agent factory. |
| `Voxa:Agent:Instructions` | string | (brief assistant) | System prompt. |
| `Voxa:Agent:ApiKey` | string | — | API key. Falls back to `Voxa:OpenAI:ApiKey`. |
| `Voxa:Agent:ConversationMemory` | bool | `true` | Per-connection bounded chat history. |
| `Voxa:Agent:MaxHistoryMessages` | int | `50` | History cap; oldest user/assistant pairs trimmed first. |
| `Voxa:Models:CachePath` | string | OS cache dir | Local-tier model cache root. `VOXA_MODEL_CACHE` env var overrides. |
| `Voxa:Models:Offline` | bool | `false` | Never download; a missing model is a startup error with provisioning instructions. |
| `Voxa:Models:EagerWarmup` | bool | `true` | Resolve + pre-load local models at startup so the first caller never pays a download or model load. |

## Why not just call Voice Live (or OpenAI Realtime) directly

Voice Live is great for the simple case. But the moment you have a second backend (regional fallback, premium voices Voice Live doesn't host, OpenAI Realtime, telephony), one tenant policy that diverges, or an audit/cost/observability concern that crosses backends, you need a pipeline. Voxa is that pipeline. The Voice Live composite processor is one node in it; you can swap it for an Azure Speech STT → MAF agent → Azure Speech TTS chain on the same wire.

The same `AzureVoiceLiveProcessor` speaks **Azure Voice Live**, **Azure OpenAI Realtime**, and **OpenAI Realtime** — they share a wire protocol, so only the endpoint URL and auth header change.

## Packages

### Meta-package

| Package | Description |
|---------|-------------|
| `Voxa` | **Start here.** Bundles `Voxa.AspNetCore` + all built-in speech providers + the OpenAI agent factory. `AddVoxa(configuration)` (2-arg) is the entry point. |

### Core

| Package | Description |
|---------|-------------|
| `Voxa.Core` | Frames, processors, pipeline, runner, generic `AgentLoopProcessor`. Zero external deps beyond NUlid. |
| `Voxa.AspNetCore` | `AddVoxa(configuration, configure)` (3-arg à-la-carte) + fluent `MapVoxaVoice` + `UseDefaults()`. The integration surface for ASP.NET Core hosts. |
| `Voxa.Testing` | WAV file source/sink, capturing/passthrough processors. |
| `Voxa.Transports.WebSocket` | Host-agnostic source + sink over `System.Net.WebSockets.WebSocket`. |
| `Voxa.Services.AzureVoiceLive` | Composite STT+LLM+TTS+VAD via Azure Voice Live's Realtime API. |
| `Voxa.Services.OpenAIRealtime` | Composite STT+LLM+TTS+VAD via OpenAI Realtime API (full-duplex, server-side VAD). |
| `Voxa.Services.MicrosoftAgents` | `MicrosoftAgentVoice.CreateProcessor(agent, options)` — wraps any MAF `AIAgent` as a configured `AgentLoopProcessor`. |
| `Voxa.Services.SpeechToSpeech` | Full-duplex speech-to-speech composite seam (VRT-005) — slots in where the cloud realtime composites do, driven by an in-process `ISpeechToSpeechSession`. |
| `Voxa.Transports.Telephony` | Vendor-neutral phone-call transport (VTL-001): media-stream source/sink over WebSocket, G.711 μ-law codec, 8 kHz resample bridge, barge-in purge. No WebRTC. |
| `Voxa.Transports.Twilio` | `MapVoxaTwilioVoice()` for Twilio Media Streams + `X-Twilio-Signature` validation, on the telephony base. |
| `Voxa.Observability` | `TracingProcessor` + `VoxaActivities` ActivitySource for OpenTelemetry. |
| `Voxa.Cli` | The `voxa` command-line tool: headless transcribe/say, model-cache management, config validation. |
| `Voxa.Mcp` | MCP server (`voxa-mcp`, stdio) giving any MCP-aware agent a voice and ears via the keyless local tier: `voxa_speak` + `voxa_transcribe`. |

### Speech (granular STT/TTS, multi-vendor)

| Package | STT | TTS | Description |
|---------|-----|-----|-------------|
| `Voxa.Speech.Abstractions` | — | — | `ISpeechToTextEngine`, `ITextToSpeechEngine`, generic `SpeechToTextProcessor` / `TextToSpeechProcessor`, `SilenceGateProcessor` (energy VAD), `TranscriptionFilter` (drops Whisper hallucinations), `SentenceAggregator` (LLM tokens → sentence-sized TTS chunks). |
| `Voxa.Speech.Azure` | ✅ | ✅ | Azure Cognitive Services Speech SDK. |
| `Voxa.Speech.OpenAI` | ✅ | ✅ | Whisper REST + OpenAI TTS (`/v1/audio/speech`). Works against OpenAI-compatible proxies. |
| `Voxa.Speech.Deepgram` | ✅ | — | Deepgram streaming STT over WebSocket (interim + locked-final segments). |
| `Voxa.Speech.AssemblyAI` | ✅ | — | AssemblyAI Universal-Streaming STT over WebSocket (cumulative turns, `end_of_turn` finals). |
| `Voxa.Speech.Gladia` | ✅ | — | Gladia real-time STT over WebSocket. |
| `Voxa.Speech.Speechmatics` | ✅ | — | Speechmatics real-time STT over WebSocket. |
| `Voxa.Speech.Google` | ✅ | — | Google Cloud Speech-to-Text v2 streaming (official gRPC client). |
| `Voxa.Speech.Aws` | ✅ | — | AWS Transcribe streaming (official SDK). |
| `Voxa.Speech.Groq` | ✅ | — | Groq Whisper (`whisper-large-v3-turbo`) via the OpenAI-compatible batch API. |
| `Voxa.Speech.Together` | ✅ | — | Together AI Whisper via the OpenAI-compatible batch API. |
| `Voxa.Speech.ElevenLabs` | — | ✅ | Streaming TTS, voice cloning, voice settings. |
| `Voxa.Speech.Mistral` | — | ✅ | Voxtral-TTS via Mistral's OpenAI-compatible audio API. |
| `Voxa.Speech.WhisperCpp` | ✅ | — | **Local, API key: none.** whisper.cpp on your CPU (via Whisper.net). VAD-gated per-utterance transcription; models SHA-256-pinned, first-run download. |
| `Voxa.Speech.Voxtral` | ✅ | — | **Local, open-weights, heavy tier.** Mistral Voxtral-Mini-4B-Realtime streaming STT served by a local vLLM (needs a ≥16 GB GPU). |
| `Voxa.Speech.Piper` | — | ✅ | **Local, API key: none.** Piper as a pooled warm child process — the fast local voice (RTF ≈ 0.05 on CPU). |
| `Voxa.Speech.Kokoro` | — | ✅ | **Local, API key: none.** Kokoro-82M in-process on ONNX Runtime — the quality local voice (24 kHz, rivals cloud voices). |
| `Voxa.Speech.Sidecar` | — | ✅ | Expressive/multilingual/voice-cloning TTS (XTTS / OpenVoice) via an out-of-process sidecar over stdio. Opt-in heavy tier. |

### Audio

| Package | Description |
|---------|-------------|
| `Voxa.Audio.Abstractions` | The mic-path seams before the VAD: `IEchoCanceller` (VRT-003, barge-in over speakers) and `IAudioEnhancer` (VLS-004, spectral denoise), each with passthrough defaults; `LinearResampler`. Seams, not DSPs. |
| `Voxa.Audio.SileroVad` | ML-based VAD using the bundled Silero VAD v5 ONNX model. Drop-in replacement for `SilenceGateProcessor` for noisy environments. |
| `Voxa.Audio.SmartTurn` | **Opt-in** smart turn detection (P0 latency). `AddVoxaSmartTurn(configuration)` plugs an `ISmartTurnClassifier` into the VAD's silence timeout — an `Http` classifier or a local Python `Sidecar` running `pipecat-ai/smart-turn-v3` — so `Voxa:Vad:StopDurationMs` can drop without clipping mid-sentence pauses. Zero-cost when unregistered. A fully in-process ONNX classifier is specced as [VLS-010](docs/specifications/vls-010-local-smart-turn-spec.html). |
| `Voxa.Audio.Onnx` | Shared ONNX Runtime session host (VLS-006): one `InferenceSession` per (path, device) process-wide, CPU by default, GPU execution providers strictly opt-in. |
| `Voxa.Audio.Diarization` | Speaker diarization seams + pure-C# clustering pipeline (VLS-005) — "who spoke when", filling `TranscriptionFrame.SpeakerId`. |
| `Voxa.Audio.Diarization.Onnx` | Reference pyannote segmentation-3.0 (MIT) implementation on the shared ONNX host. |

Mix-and-match: use any STT vendor with any LLM with any TTS vendor.

## Architecture

```
┌──────────────┐   ┌──────────────┐   ┌──────────────┐
│PipelineSource│-->│FrameProcessor│-->│ PipelineSink │
└──────────────┘   └──────────────┘   └──────────────┘
       │                  ↑                  │
   IngestAsync       ErrorFrame         ReadAllAsync
                    (upstream)
```

Each `FrameProcessor` runs two concurrent tasks: a **system task** draining priority frames (`InterruptionFrame`, speaking events, errors) and a **data task** draining ordered frames. An interruption mid-frame cancels the in-flight data frame's `CancellationToken` so long-running calls (LLM streaming, TTS synthesis) abort cleanly — while frames marked `IUninterruptible` (tool calls, `EndFrame`) are guaranteed to survive it. The data loop is allocation-free at steady state: the per-frame cancellation source is reused and only replaced after an interruption fires it.

## The agent loop

`Voxa.Core.AgentLoopProcessor` is a framework-agnostic per-turn agent processor. It owns the bookkeeping every voice-agent integration would otherwise re-implement:

- **Data-loop / turn-worker split** — the data loop never blocks on agent calls or tool round-trips, so frames keep flowing while a turn is running.
- **Per-turn id** + lifecycle frames (`LlmTurnStartedFrame` / `LlmTurnEndedFrame`) for clean turn boundaries downstream.
- **Frontend-tool TCS correlation** — tool calls round-trip through the pipeline (server emits `ToolCallRequestFrame`, client returns `ToolCallResultFrame`); the agent re-runs inline with the result appended.
- **Per-turn try/catch isolation** — a failed turn emits an upstream `ErrorFrame` and the worker drains the next queued transcription.
- **Token aggregation** — `LlmUsageFrame`s yielded by drivers roll into `TurnSummary.Usage` for hosts to record in `OnTurnCompleted`.

Hosts plug a runtime in by implementing `IAgentTurnDriver`. For Microsoft Agent Framework that's done for you — `MicrosoftAgentVoice.CreateProcessor(agent, options)` returns a fully-configured `AgentLoopProcessor`:

```csharp
voice.UseMicrosoftAgent(agent, options =>
{
    options.BuildMessages = (turn, ct) => LoadMyHistoryAsync(turn.UserText, ct);
    options.IsFrontendTool = name => myFrontendCatalog.Contains(name);
    options.BuildBackendToolStatus = name => name switch
    {
        "pf_get_spending_summary" => "Checking your spending...",
        _ => null,
    };
    options.OnTurnCompleted = (turn, summary, ct) => RecordAuditAsync(turn, summary, ct);
});
```

## Backend tool progress pattern

Voice agents commonly run a backend (read-only) tool mid-turn — "What are my top expenses?" → acknowledgement text → backend lookup → final answer text. Voxa supports this naturally:

1. The model streams the acknowledgement as `TextContent`. Voxa yields it as `LlmTextChunkFrame` immediately, so `SentenceAggregator` flushes the sentence and TTS starts speaking *before* the backend tool runs.
2. The model then emits a backend `FunctionCallContent`. MAF auto-executes the tool synchronously (raw tool names are never surfaced to the client).
3. While the tool runs, Voxa optionally emits a sanitized `StatusFrame("Checking your spending...")` for the client UI — opt-in via `MicrosoftAgentVoiceOptions.BuildBackendToolStatus`.
4. The model then streams the final answer text and any frontend display tools (`display_spending_pie_chart`, etc.) round-trip through the pipeline as normal.

The transport ships the status as `{ "type": "status", "message": "..." }` over the WebSocket. Hosts on a different transport can drop the frame or wrap it in their own envelope.

## Two pipeline shapes

**Voice Live path** — managed STT+LLM+TTS+VAD in a single processor:

```csharp
Pipeline.Build()
    .Source(new WebSocketAudioSource(ws))
    .Then(new AzureVoiceLiveProcessor(voiceLiveOpts))
    .Sink(new WebSocketAudioSink(ws));
```

**Granular path** — vendor-neutral STT + agent + TTS. Mix any STT, any LLM, any TTS:

```csharp
Pipeline.Build()
    .Source(new WebSocketAudioSource(ws))
    .Then(AzureSpeech.StreamingTranscription(azureOpts))     // STT vendor
    .Then(MicrosoftAgentVoice.CreateProcessor(yourAgent))    // any MAF agent
    .Then(new SentenceAggregator())
    .Then(ElevenLabs.Synthesis(elevenlabsOpts))              // TTS vendor
    .Sink(new WebSocketAudioSink(ws));
```

## Vendor recipes

Each STT vendor pairs with each TTS vendor pairs with any agent. Some examples:

```csharp
// Azure end-to-end (cheapest, broadest regional coverage)
.Then(AzureSpeech.StreamingTranscription(azure))
.Then(MicrosoftAgentVoice.CreateProcessor(agent))
.Then(AzureSpeech.Synthesis(azure))

// Whisper STT, OpenAI TTS, OpenAI agent — full OpenAI stack
.Then(OpenAISpeech.StreamingTranscription(openai))
.Then(MicrosoftAgentVoice.CreateProcessor(openaiAgent))
.Then(OpenAISpeech.Synthesis(openai))

// Premium voice — Whisper + ElevenLabs voice clone
.Then(OpenAISpeech.StreamingTranscription(openai))
.Then(MicrosoftAgentVoice.CreateProcessor(agent))
.Then(ElevenLabs.Synthesis(elevenlabs))

// Cost-optimised — Azure STT (fast, cheap) + Mistral TTS (Voxtral)
.Then(AzureSpeech.StreamingTranscription(azure))
.Then(MicrosoftAgentVoice.CreateProcessor(agent))
.Then(Mistral.Synthesis(mistral))
```

## Wire protocol

Binary WebSocket frames carry raw 16-bit PCM @ 24 kHz mono. Text WebSocket frames carry typed JSON envelopes:

**Client → Server:** `hello`, `end`, `text`, `toolResult`
**Server → Client:** `session`, `transcription`, `text`, `toolCall`, `speaking`, `interruption`, `status`, `error`, `end`

The `session` envelope is sent once at connection start and announces the input/output sample rates the pipeline is operating at — clients use it to configure their audio encoder/decoder without hardcoding sample rates. Old clients that do not recognise the type safely ignore it.

`WebSocketAudioSink` accepts a `customSerializer` hook so hosts can add their own envelopes (e.g. AONIK's `threadReady`) without subclassing.

Envelopes are serialized straight to UTF-8 via `System.Text.Json` source generation — no reflection, no intermediate strings, one allocation per envelope (zero for the fixed `interruption`/`end` envelopes). The wire format is locked byte-for-byte by compatibility tests, so existing clients are unaffected.

See [`WireProtocol.cs`](src/Voxa.Transports.WebSocket/Protocol/WireProtocol.cs) for the codec.

**Official JS client** — [`@voxa/client`](clients/voxa-client) (npm, prerelease) speaks this
protocol for you: mic capture, PCM playback with barge-in flush on the `interruption` envelope,
sample-rate negotiation from the `session` envelope (which carries the protocol version), typed
envelope handlers. The browser test pages in the samples are built on it.

## Performance

Voxa's hot paths are engineered for real-time audio — GC pauses are the worst failure mode for a voice pipeline, so the steady-state audio path allocates (almost) nothing:

- **Frame loop:** ~25 B/frame through a processor (the per-frame linked `CancellationTokenSource` is reused, not reallocated).
- **Silero VAD:** ~272 B/inference (~18× less than naive ONNX usage) via pre-bound `OrtValue` inputs *and* outputs.
- **Transport:** single-copy binary receive; pooled buffers for fragmented messages; outbound sends drain through a single-writer queue instead of a lock held across network I/O.
- **Barge-in, end to end:** when the user interrupts, the agent loop cancels the in-flight turn and emits `InterruptionFrame`; the aggregator and TTS mute the stale tail; bot audio already queued for the socket is dropped (epoch-stamped queue) and the `interruption` envelope jumps ahead — the bot actually stops talking, and the answer doesn't resume from the next sentence.
- **TTS time-to-first-byte:** all four TTS engines stream chunk-by-chunk (Azure included, via `AudioDataStream`); HTTP engines share one connection pool (`VoxaHttp.Shared`) and pre-warm TLS at session start.
- **Latency knobs:** eager first-sentence flush (`SentenceAggregator.EagerFirstChunkMinChars`), configurable VAD hangover, and opt-in smart-turn detection (`Voxa.Audio.SmartTurn`) so `Voxa:Vad:StopDurationMs` can drop to ~200 ms without clipping speakers who pause to think.

Measured numbers live in [`bench/BASELINE.md`](bench/BASELINE.md) (BenchmarkDotNet project under `bench/`); every knob is documented with its trade-off in [`docs/performance-tuning.md`](docs/performance-tuning.md). The full engineering spec is [`docs/specifications/voxa-performance-optimization-spec.html`](docs/specifications/voxa-performance-optimization-spec.html).

## Observability

Voxa.Observability publishes `VoxaActivities.Source` (an `ActivitySource` named `Voxa`). Drop a `TracingProcessor` anywhere in the pipeline to emit per-frame spans:

```csharp
Pipeline.Build()
    .Source(...)
    .Then(new TracingProcessor("user-input"))
    .Then(new AzureVoiceLiveProcessor(opts))
    .Then(new TracingProcessor("voice-live-out"))
    .Sink(...);
```

Wire OpenTelemetry to capture them:

```csharp
services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource("Voxa").AddOtlpExporter());
```

Voxa.Core also publishes a `System.Diagnostics.Metrics` meter named `Voxa` (`VoxaMetrics.MeterName`):

| Instrument | Meaning |
|---|---|
| `voxa.turn.ttfb` | Voice-to-voice latency: user stopped speaking → first bot audio byte on the wire. |
| `voxa.sink.queue_depth` | Outbound WebSocket queue depth — sustained growth means the client/network can't keep up. |

```csharp
services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter(VoxaMetrics.MeterName).AddOtlpExporter());
```

The sample server also logs `turn ttfb {n} ms` per turn via a plain `MeterListener`, no OTel backend required.

## Sample apps

### Minimal server — five lines

[`samples/Voxa.Samples.MinimalServer`](samples/Voxa.Samples.MinimalServer) — the five-line `Program.cs` demo. Fill in `appsettings.json` with your API key and run:

```bash
dotnet run --project samples/Voxa.Samples.MinimalServer
```

Or run it **fully local with no API key at all** (`appsettings.Local.json`: WhisperCpp + Piper +
the Echo agent; first run downloads the models), then open <http://localhost:5170> and talk:

```bash
dotnet run --project samples/Voxa.Samples.MinimalServer --launch-profile Local
```

The browser test page reads the server's `session` envelope for sample rates, so any local voice
(16 kHz Piper, 22.05 kHz Piper, 24 kHz Kokoro) plays at the correct pitch.

> **First run downloads ~250 MB of models** (Whisper + the Piper voice/binary) before the server
> finishes starting — progress is logged to the console. It isn't hung; subsequent runs start
> instantly from the cache, and an air-gapped box can pre-provision it (see
> [docs/local-speech.md](docs/local-speech.md)).

### Full sample server

[`samples/Voxa.Samples.AspNetServer`](samples/Voxa.Samples.AspNetServer) — ASP.NET Core server demonstrating each pipeline shape side-by-side:

| Route | Pipeline | Surface |
|-------|----------|---------|
| `/voice/voice-live`         | Voice Live composite (full LLM-driven) | Lower-level `Pipeline.Build()` |
| `/voice/azure`              | Azure STT → echo → Azure TTS | Lower-level `Pipeline.Build()` |
| `/voice/openai`             | OpenAI Whisper → echo → OpenAI TTS | Lower-level `Pipeline.Build()` |
| `/voice/openai-realtime`    | OpenAI Realtime composite | Lower-level `Pipeline.Build()` |
| `/voice/openai-batch`       | Whisper → MAF agent → SentenceAggregator → OpenAI TTS | Lower-level `Pipeline.Build()` |
| `/voice/openai-batch-fluent` | Same as above, expressed via `MapVoxaVoice` | Fluent `Voxa.AspNetCore` |
| `/voice/azure-elevenlabs`   | Azure STT → echo → ElevenLabs TTS | Lower-level `Pipeline.Build()` |
| `/voice/azure-mistral`      | Azure STT → echo → Mistral Voxtral-TTS | Lower-level `Pipeline.Build()` |

`dotnet run --project samples/Voxa.Samples.AspNetServer`. Configure only the vendors you want to demo.

## Building

```bash
dotnet build
dotnet test

# benchmarks (BenchmarkDotNet):
dotnet run -c Release --project bench/Voxa.Benchmarks -- --filter *
```

Targets `net10.0`. Requires .NET 10 SDK.

## Roadmap

| Phase | Scope |
|-------|-------|
| 1 | ✅ Core pipeline primitives |
| 2 | ✅ AzureVoiceLive composite + Voxa.Testing harness |
| 3 | ✅ WebSocket transport + Microsoft Agents adapter |
| 5 | ✅ AzureSpeech STT/TTS standalone + ASP.NET sample |
| 5.5 | ✅ Generic `AgentLoopProcessor` + delegate-based MAF surface + fluent `MapVoxaVoice` |
| 5.6 | ✅ VPS-001 performance pass — zero-allocation hot path, source-generated wire protocol, streaming Azure TTS, server-side barge-in purge, `voxa.turn.ttfb` metric, benchmark suite |
| P5 | ✅ VDX-001 developer experience — `AddVoxa()` + `UseDefaults()`, typed config, named latency profiles, provider descriptors, `Voxa` meta-package, fail-fast startup validation, conversation memory, `session` wire envelope |
| P6 (partial) | ✅ VLS-001 local/offline speech tier — `WhisperCpp` STT, `Piper` + `Kokoro` TTS, SHA-256-pinned model cache with offline mode, keyless `Echo` agent, startup warm-up, zero-network CI conversation lane ([docs](docs/local-speech.md)) |
| P8 | ✅ VST-001 Voxa Studio — desktop app with live VAD trace + latency waterfall over the new `VoxaDiagnosticsHub` pipeline event stream (also closes the P7 stage-latency item: `voxa.stage.latency`), voice lab, model-cache manager, config composer ([docs](docs/studio.md)) |
| P8.5 | ✅ VST-002 Studio 2.0 — brand + animated mark + splash, STT/TTS playgrounds (WER harness, A/B/X, batch bench), node-canvas pipeline builder with run-from-canvas and honest exporters, run & metrics workbench with scripted decks and run compare |
| P8.6 | ✅ Studio settings & profiles — provider activation with DPAPI-encrypted credentials, app-wide named pipeline profiles, Models/Voices provider filters |
| P0 | ✅ Smart turn detection — `ISmartTurnClassifier` seam through the VAD + composer; opt-in `Voxa.Audio.SmartTurn` (HTTP classifier + local Python sidecar running `pipecat-ai/smart-turn-v3`); Studio toggle |
| M2–M7 | ✅ Robustness & local-tier seams — eager/speculative STT + turn-taking knobs (VRT-002), `IEchoCanceller` (VRT-003), interim coalescing (VRT-004), `IAudioEnhancer` denoise (VLS-004), speaker diarization (VLS-005), shared ONNX host (VLS-006), speech-to-speech composite seam (VRT-005) |
| — | ✅ STT vendor breadth — Deepgram, AssemblyAI, Gladia, Speechmatics, Google, AWS Transcribe, Groq, Together (8 new packages on shared streaming/batch bases) |
| — | ✅ VTL-001 telephony — Twilio Media Streams transport (`MapVoxaTwilioVoice`), vendor-neutral telephony base, μ-law codec, TwilioServer sample |
| — | ✅ VDX-005 `@voxa/client` — official JS/npm client (versioned protocol, barge-in-completing playback) + npm release lane |
| — | ✅ VDX-006/007 host seams — `IVoiceAgentConfigurator` (own your conversation memory) + host-registered `IAgentTurnDriver` under `UseDefaults()` |
| — | ✅ VDX-008 background agent delegation — talker/thinker split, `delegate_task`, relevance-gated result delivery, Studio badge + Builder node ([guide](docs/background-agent.md)) |
| — | ✅ Barge-in fix — granular chain cancels the in-flight turn on user speech (`InterruptionFrame` + aggregator/TTS stale-tail mute) |
| **Next (specced)** | Turn-taking program — backchannel-aware interruption gating ([VRT-006](docs/specifications/vrt-006-turn-taking-strategies-spec.html)), local on-device end-of-turn model ([VLS-010](docs/specifications/vls-010-local-smart-turn-spec.html)), behavioral conversation evals ([VDX-009](docs/specifications/vdx-009-behavioral-evals-spec.html)), pipeline health watchdogs ([VRT-007](docs/specifications/vrt-007-pipeline-health-watchdogs-spec.html)) |
| 6 | Observability, OSS release, NuGet publish, CI |
| 4 | Mobile client integration (downstream consumers) |

(Phase 4 swapped to last since it lives in consuming repos, not Voxa itself.) Session resilience, AONIK integration, and the rest of the backlog are tracked with detail in [`ROADMAP.md`](ROADMAP.md).

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). All contributions need an issue or design doc reference for non-trivial changes.

## License

MIT. See [LICENSE](LICENSE).
