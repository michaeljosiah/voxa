# Voxa Studio

A desktop app that runs the real Voxa pipeline against your microphone and speakers and lets
you **watch it think**: a live VAD probability trace, a per-turn latency waterfall, standalone
STT and TTS playgrounds, a node-canvas pipeline builder, a model-cache manager, and a config
composer. Specced as [VST-001](specifications/voxa-studio-spec.html); the v2 brand, splash,
playgrounds, and builder follow the
[VST-002 design brief](specifications/voxa-studio-design-brief.html).

Studio is keyless out of the box — its default pipeline is the local tier from
[VLS-001](local-speech.md) (WhisperCpp STT + Echo agent + Piper TTS). Cloud providers light up
automatically if their keys are present in environment variables or user-secrets; nothing
requires them.

## Run it

```bash
dotnet run --project apps/Voxa.Studio
```

Windows is the supported platform in v1 (the audio backend is WASAPI). The app builds and the
UI runs on macOS/Linux, but live Talk capture/playback needs an audio backend that doesn't
exist yet — the playgrounds' synthesis/transcription, the Models view, and the Config composer
still work there (minus mic recording and speaker playback).

Nothing downloads at launch. The first **Talk** session (or the first voice you audition)
downloads the pinned models with visible progress — `tiny.en` (~75 MB) plus the Piper voice and
binary (~80 MB) for the default config.

## The five views

### Talk

Pick a microphone and speaker, press **Start session**, speak.

- **Transcript** — your utterances (final Whisper transcripts) and the bot's streamed replies.
  A barge-in cuts playback instantly and marks the bubble `⊘ interrupted`.
- **VAD probability** — the Silero speech probability per 32 ms window, live, with the
  configured threshold drawn as a dashed rule and gate-open intervals shaded. If this line
  stays flat at zero while you talk, you've found a VAD problem in seconds (the class of bug
  that previously took a bisection session — see the VLS-001 history).
- **Turn waterfall** — for every completed turn, how the response time split:
  `VAD` (silence hangover) → `STT` (final transcript) → `AGENT` (first token) → `TTS`
  (first audio byte) → `OUT` (device enqueue). The sum is your voice-to-voice latency; the
  widest block is what to fix. The same numbers are recorded on the `voxa.stage.latency`
  histogram for any host with diagnostics enabled.
- **Event log** — the raw diagnostics stream (sequence numbers, timestamps), for when you need
  exactly what fired and in what order.

### Playgrounds

Two standalone labs behind one segmented switch (VST-002 §6–7) — measure each end of the
pipeline without composing the whole thing.

**STT lab.** Answer "how well and how fast does speech become text on this machine?" for any
pinned Whisper model:

- **Three sources** — the bundled `jfk.wav` fixture (replayable, known content, ships in-repo),
  any WAV you drop on the view or browse to (stereo and odd sample rates are converted), or the
  live mic (toggle record, max 30 s; transcribes when you stop).
- **Final cards** — each utterance lands as a card with its waveform, the model that ran, the
  utterance duration, and the **final-transcript latency** (utterance end → final) measured
  standalone on your hardware.
- **Accuracy harness** — paste what was actually said and get a live **WER** with the
  alignment colored on the transcript: substitutions amber, insertions cyan, deletions red.
  This turns "tiny.en sounds fine" into "tiny.en is 8.1% WER on my accent".
- **Side-by-side** — run the same audio through two models (sequentially; one whisper context
  at a time) and get the trade-off in one sentence: WER vs WER, and who was slower by how much.

**TTS lab.** The v1 Voice Lab matured into a lab. Everything from v1 carries over — the full
Piper + Kokoro catalog through the real engines, **TTFB**/**RTF** on your hardware, **A**/**B**
pins, `⤓ wav` export — plus:

- **Take history** — every synthesis lands as a replayable take with its waveform; the newest
  take's waveform is the **playback scrubber** (click or drag to seek).
- **A/B/X blind test** — X is randomly A or B; vote, then reveal. Settles "Kokoro is obviously
  better" with data instead of expectation bias.
- **Stress phrases** — a one-click deck of the sentences that actually break TTS: currency and
  dates, code identifiers, homographs ("the bandage was wound around the wound"), diacritics,
  long clauses.
- **Batch bench** — synthesize the whole deck on every checked voice (sequentially, so the
  numbers don't fight for cores) → TTFB p50/p95 + mean RTF per voice, exportable as CSV.

Playback, recording, and the bench pause while a Talk session owns the audio device.

### Builder

A node canvas over the live provider registry (VST-002 §8): palette → canvas → inspector.
Voxa pipelines are a linear chain — there is no fan-out in the runtime — so the canvas enforces
single-in/single-out wiring and makes that feel intentional rather than limited.

- **Typed ports.** Every port carries a frame type (grey audio, cyan transcription, violet
  agent-text, amber synth-audio — the stage palette). An incompatible wire snaps back with the
  reason in words ("Echo emits agent-text frames; WhisperCpp consumes audio"). A dangling port's
  **+** offers only type-compatible follow-ups, so beginners never see an invalid wire.
- **Inspector.** Selecting a node edits its options — Whisper model, TTS voice, VAD threshold
  and stop duration (sliders), agent model/key, capture/render device — with cached state shown
  on the node itself.
- **Run graph.** The canvas compiles to the same `Pipeline.Build().Source().Then(…).Sink()`
  chain Talk uses, in an ephemeral container layered over the live config (the app's own
  configuration is untouched), and starts a live session in place. While live, the canvas is
  the instrument: edges pulse on real frame events (gate-open shimmer, one pulse per final
  transcript / agent delta / TTS chunk), the active stage node glows with its measured latency,
  per-node queue depths surface backpressure, and the latest turn renders as a slim waterfall
  strip along the canvas bottom. Every signal is a real `VoxaDiagnosticsHub` event.
- **Export.** When the chain matches what `UseDefaults()` composes, export the
  `appsettings.json` block; any other valid shape (say, no `TranscriptionFilter`) exports as
  generated C# composition code instead — both honest artifacts a server can run. API keys are
  never exported.
- **Furniture.** Drag to arrange (snap-to-grid), **Tidy** auto-layout, Ctrl+wheel zoom, undo/redo
  (Ctrl+Z/Y), save/load the graph as JSON in your user profile. The canvas opens with the active
  config as a graph, and the Config view's **Open in Builder** does the same for any draft.

Run is disabled while a Talk session owns the audio device, and vice versa.

### Models

The model cache, visible: every entry with size and engine, the effective cache root and where
it came from (`VOXA_MODEL_CACHE` → `Voxa:Models:CachePath` → OS default).

- **verify** re-hashes an entry against its pinned SHA-256 — catches on-disk corruption.
- **purge** deletes an entry (refused while another Voxa process holds its download lock).
- **Prefetch full catalog** downloads every pinned model for this machine — then copy the
  cache folder to an offline box and set `Voxa:Models:Offline=true` there. That's air-gap
  provisioning, one button.

### Config

Build a pipeline from dropdowns populated by the **live provider registry** — the choices can't
drift from the code. Validation runs the exact options path a server boots with (plus the agent
factory's credential check), so *valid here means the exported block boots there*. Copy the
generated `appsettings.json` or save it to a file.

**Talking to a real LLM.** The default agent is the keyless `Echo` parrot. To have the pipeline
answer with a model:

1. Set **Agent** to `OpenAI`.
2. Enter the chat model (e.g. `gpt-4o-mini`) and your API key. Leave the key blank to fall back
   to what the environment already provides (`Voxa__OpenAI__ApiKey` or user-secrets) — the
   validation dot tells you immediately whether a usable key was found.
3. Press **⚡ Apply to Studio**. The running app rebuilds with the draft (a server restart,
   without the restart) and the next Talk session streams the model's replies through TTS —
   complete with the agent's first-token latency in the waterfall.

Apply is disabled while a Talk session is live (stop it first). The API key is applied to the
running app only — it is **never** written into the exported JSON or to disk; for servers, use
user-secrets or the environment variable. Apply works for every selection here, not just the
agent: swap Piper→Kokoro or change the Whisper model the same way.

## Diagnostics for servers (not just Studio)

Studio's Talk view is a renderer over a framework feature: `VoxaDiagnosticsHub`
(`Voxa.Diagnostics`). Any host can enable it:

```json
"Voxa": { "Diagnostics": { "Enabled": true } }
```

and subscribe to the per-session hub (registered scoped by `AddVoxa`) for the same typed event
stream — VAD windows, turn edges, transcripts, stage latencies. With diagnostics off (the
default), the composed pipeline is byte-identical to one without diagnostics support; with it
on but unobserved, the cost is one boolean check per frame. Stage latencies also land on the
`voxa.stage.latency` histogram (tag `stage`) next to `voxa.turn.ttfb`.

## Troubleshooting

| Symptom | Likely cause / fix |
|---|---|
| Start session fails with an audio error | Exclusive-mode WASAPI device — switch the device to shared mode, or pick another endpoint. |
| No devices in the pickers | No active capture/render endpoints (or non-Windows). Check the OS sound settings, then the refresh on re-entering the view. |
| First session is slow to start | First-run model download (~155 MB for the defaults) — progress shows in the status line. Subsequent sessions are offline. |
| VAD trace flat at zero while speaking | Mic muted / wrong device, or genuinely a VAD regression — check the trace against the threshold rule. |
| `verify` shows ✗ | The cached file is corrupt. Purge it; the next session re-downloads through the SHA-256-verified resolver. |
| Playground play/record/bench disabled | A Talk session owns the audio device — stop it first. |
| STT lab rejects my audio file | Only 16-bit PCM WAV is read (stereo/other rates are converted). Re-export compressed formats as PCM WAV first. |
