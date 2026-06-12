# Voxa Studio

A desktop app that runs the real Voxa pipeline against your microphone and speakers and lets
you **watch it think**: a live VAD probability trace, a per-turn latency waterfall, a voice
audition lab, a model-cache manager, and a config composer. Specced as
[VST-001](specifications/voxa-studio-spec.html).

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
exist yet — Voice Lab synthesis, the Models view, and the Config composer still work there.

Nothing downloads at launch. The first **Talk** session (or the first voice you audition)
downloads the pinned models with visible progress — `tiny.en` (~75 MB) plus the Piper voice and
binary (~80 MB) for the default config.

## The four views

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

### Voices

Every pinned Piper and Kokoro voice. Type a sentence, press ▶ — synthesis goes through the
real engines (Piper's warm process pool, Kokoro's ONNX session), so **TTFB** and **RTF**
are real numbers for *your* hardware, not someone's benchmark. Pin two voices to **A**/**B**
slots for instant comparison; `⤓ wav` exports the take to your Music folder.

Playback pauses while a Talk session owns the output device.

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
| Voice Lab play button disabled | A Talk session owns the output device — stop it first. |
