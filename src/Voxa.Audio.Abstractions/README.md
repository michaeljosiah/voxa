# Voxa.Audio.Abstractions

Audio-stage abstractions for the Voxa pipeline — the seams that sit on the mic path *before* the VAD.

## `IEchoCanceller` (VRT-003)

The acoustic echo canceller seam for **barge-in over speakers**. While the bot is speaking, its own audio
loops back through the speakers into the mic; a half-duplex gate avoids transcribing it, but that gate is
what blocks true talk-over-the-bot. An echo canceller subtracts the bot's audio (the *far-end* reference)
from the mic (the *near-end*) so the VAD/STT see only the user.

This package ships the **seam, a passthrough default, and the wiring** — not a DSP:

- `IEchoCanceller` — `FeedReference(farEndPcm)`, `CancelEcho(nearEndPcm) → pcm`, `Reset()`, `SampleRate`.
  Buffering, frame alignment, and resampling between far-end and near-end are the implementation's job.
- `NullEchoCanceller` — passthrough; returns the mic audio unchanged and ignores the reference.
- `EchoCancellerProcessor` — runs `CancelEcho` per `AudioRawFrame` (placed before the VAD); resets on
  session start and on an interruption epoch.
- `EchoReferenceTapProcessor` — feeds each outbound bot `AudioRawFrame` into the canceller as the far-end
  reference (placed after the TTS stage); observes only, forwards unchanged.

Enable a real canceller with `Voxa:Aec:Engine` once an implementation package is referenced; with it unset
or `None` the composer inserts **no** AEC stage, so the pipeline is byte-identical to today. A production DSP
(WebRTC APM / SpeexDSP / a managed canceller) is a separate, opt-in follow-up package.
