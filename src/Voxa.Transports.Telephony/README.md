# Voxa.Transports.Telephony

Shared **telephony media-stream transport** for the [Voxa](https://github.com/michaeljosiah/voxa) voice
pipeline (VTL-001). Runs a phone call in and out of a Voxa pipeline **over a WebSocket** — no WebRTC, no
SIP, no ICE/TURN.

Telephony providers (Twilio Media Streams, Azure ACS) bridge the PSTN/WebRTC side themselves and hand you
**JSON messages over a WebSocket** whose payload is base64-encoded audio. This package is the vendor-neutral
half of that:

- `TelephonyMediaStreamSource` / `TelephonyMediaStreamSink` — a `PipelineSource`/`PipelineSink` pair that own
  the WebSocket read/write loops, the bounded outbound queue with the **barge-in epoch purge**, and the
  **8 kHz ↔ pipeline-rate resample bridge**.
- `ITelephonyMediaCodec` — the small per-vendor seam (parse one inbound message → a transport event;
  serialize outbound audio / clear / mark). The base does the μ-law decode and the resample, so a codec is
  just JSON framing.
- `MuLaw` — allocation-free G.711 (μ-law / PCMU) encode/decode.

The VAD → STT → agent → TTS chain in the middle is **untouched**: the same `DefaultVoicePipelineComposer`
that builds the native WebSocket route builds the telephony route. Telephony is purely an edge skin.

The **Twilio** codec + TwiML endpoint live in
[`Voxa.Transports.Twilio`](https://www.nuget.org/packages/Voxa.Transports.Twilio). This base package has no
ASP.NET dependency — it operates over any `System.Net.WebSockets.WebSocket`.

Pre-alpha; the public API still moves.
