# Voxa performance tuning

Knobs introduced by the [VPS-001 performance work](specifications/voxa-performance-optimization-spec.html),
plus the metrics to watch. Most defaults are tuned for correctness; this page is for squeezing
latency once the pipeline works end-to-end.

## Metrics

Voxa publishes a `System.Diagnostics.Metrics` meter named **`Voxa`** (`VoxaMetrics.MeterName`):

| Instrument | Unit | Meaning |
|---|---|---|
| `voxa.turn.ttfb` | ms | Voice-to-voice latency: user stopped speaking → first bot audio byte sent. The number to minimize. |
| `voxa.sink.queue_depth` | frames | Outbound WebSocket queue depth at enqueue time. p99 should stay low (< ~8); sustained growth means the client/network can't keep up with TTS. |

Wire it up with OpenTelemetry (`meterProviderBuilder.AddMeter(VoxaMetrics.MeterName)`), or, like the
sample server, attach a `MeterListener` and log `voxa.turn.ttfb` per turn.

## Latency knobs

| Option | Default | Effect | Trade-off |
|---|---|---|---|
| `SileroVadOptions.StopDuration` | 800 ms | Silence required before the gate closes and the turn ends. | Lower (400–500) = snappier turns but cuts off speakers who pause to think. Raise (1200–1500) for slow speakers. The proper fix for the tension is `ConfirmTurnEnd`. |
| `SileroVadOptions.StartDuration` | 200 ms | Sustained voiced audio before the gate opens. | Lower = faster trigger but more false opens on brief noises. |
| `SileroVadOptions.PrerollDuration` | 300 ms | Audio replayed when the gate opens so the first syllable isn't lost. | Larger = safer onset capture, slightly more audio to STT. |
| `SileroVadOptions.ConfirmTurnEnd` | `null` | Smart-turn seam. When set, the silence timeout asks this callback "is the turn really over?" — return `false` to treat it as a mid-sentence pause. Lets `StopDuration` be aggressive (e.g. 200 ms) safely. | Adds a classifier call per turn-end candidate. The classifier itself (LLM/ONNX/HTTP) is a separate component that plugs in here. |
| `SileroVadOptions.EagerSttDelay` | `null` (off; on in LowLatency/Cheap) | Speculative STT (VRT-002). At this silence delay (strictly `< StopDuration`) the VAD flushes STT speculatively so transcription overlaps the rest of the hangover — the transcript is often ready by the time the turn is confirmed. | Earlier transcripts on a clean end-of-turn; wasted (suppressed) STT work when the user resumes. Set ≈ `StopDuration − 250 ms`. Config: `Voxa:Vad:EagerSttDelayMs`. |
| `SileroVadOptions.MaxUtteranceDuration` | `null` (off) | Force-split (VRT-002). Caps a single open-gate utterance; on reaching it the VAD emits an intermediate end-of-turn (flushing STT) and re-opens a fresh utterance. | Bounds memory and yields periodic transcripts for a non-pausing speaker / stuck-open mic; too low chops natural sentences. Config: `Voxa:Vad:MaxUtteranceDurationMs`. |
| `VoxaAgentOptions.MaxResponseDuration` | `null` (off; 30 s in LowLatency/Cheap) | Response cap (VRT-002). Bounds a single turn's wall-clock output; a runaway/looping LLM is truncated and the turn closed cleanly (`LlmTurnEndedFrame` still fires). | A capped turn is a normal truncated completion, not an error. Config: `Voxa:Agent:MaxResponseDurationMs`. |
| `SpeechToTextProcessor.InterimMinInterval` | ~150 ms | Streaming STT (VRT-004). Throttles interim (`IsFinal:false`) transcripts to ≤ one per window so a chatty streaming engine can't flood the bounded data channel. Interims are display/turn-signal only — the agent acts on finals; finals are never coalesced. Config: `Voxa:InterimMinIntervalMs`. | Larger = fewer caption updates, less channel load; smaller = snappier live caption, more frames. |
| `SentenceAggregator.EagerFirstChunkMinChars` | 0 (off) | On the **first** flush of a turn, also break at a clause boundary (`, ; :`) once this many chars are buffered — gets the opening audio out 100–400 ms sooner. Later flushes use full sentence boundaries. | Slightly more, shorter first chunk. 40 is a good starting value (used by the sample server). |
| `SentenceAggregator.MaxBufferChars` | 500 | Hard cap that forces a flush even without a boundary, so TTS never stalls on a runaway response. | — |

**Eager ↔ smart-turn precedence (VRT-002).** Eager dispatch is a *bet* that the turn ended; `ConfirmTurnEnd`
is the *authority* on whether it did. Precedence is one-directional: a `ConfirmTurnEnd → false` (or a resumed
voiced window inside the eager window) always **supersedes** a pending eager pass — its utterance id is marked
stale and `SpeechToTextProcessor` drops the speculative final before it becomes a turn (the per-frame
cancellation token does not reach STT inference, so suppression by id, not cancellation, is the guarantee). So
eager STT is safe to combine with smart turn: `LowLatency`/`Cheap` enable both; `Quality` leaves eager off to
favour smart-turn accuracy. A misconfigured `EagerSttDelay ≥ StopDuration` is disabled with a warning.

## Provider notes

- **Streaming TTS**: OpenAI, ElevenLabs, Mistral, and (after VPS-001) **Azure** all stream audio
  chunk-by-chunk. Azure now uses `StartSpeakingTextAsync` + `AudioDataStream` for ~150 ms
  time-to-first-byte instead of buffering the whole utterance.
- **Connection warmup**: HTTP speech engines (OpenAI TTS/Whisper, ElevenLabs, Mistral) pre-establish
  TCP+TLS on `StartAsync` when using the shared client, so the first synthesis of a session skips
  the handshake. They share one `VoxaHttp.Shared` connection pool — inject your own `HttpClient`
  only if you need custom policies.
- **STT flush**: `OpenAISpeechOptions.SttBufferSeconds` (default 30 s) is a safety backstop only —
  the real flush is VAD-driven (`UserStoppedSpeakingFrame`). Set to 0 to rely entirely on VAD.

## Throughput / allocation knobs

- **`BoundedChannelOptions`** can be passed to a custom `FrameProcessor` to tune its data-channel
  capacity (default 64) and backpressure mode. The realtime processors use capacity 256 with
  `DropOldest`.
- **`WebSocketAudioOptions.ReadBufferSize`** (default 16 KB) sizes the transport receive buffer.
  Audio chunks (~640 B at 20 ms/16 kHz) fit the single-receive fast path comfortably.

## Barge-in

The `WebSocketAudioSink` drops queued bot **audio** from before an interruption (epoch purge), so the
bot stops almost immediately when the user speaks over it; non-audio frames (transcriptions, tool
calls, status) are never dropped. The client should handle the `{"type":"interruption"}` envelope by
flushing its local playback buffer.

### Echo cancellation seam (VRT-003)

Real barge-in over **speakers** (not headphones) needs the bot's own audio removed from the mic before the
VAD sees it — otherwise the bot hears itself. Voxa ships the **seam**, not a DSP: `IEchoCanceller` in
`Voxa.Audio.Abstractions`, an `EchoCancellerProcessor` placed *before* the VAD, and a far-end tap that feeds
the bot's outbound TTS audio as the reference. The default (`Voxa:Aec:Engine` unset or `None`) inserts **no**
AEC stage and no tap, so the composed pipeline is byte-identical to today. A real canceller ships as a
separate opt-in `Voxa.Audio.Aec.*` package (WebRTC APM / SpeexDSP / managed); reference it and set
`Voxa:Aec:Engine` to its registered name to enable it. Buffering, frame alignment, and resampling between the
far-end and near-end streams are the implementation's responsibility — the seam stays deliberately simple.

### Speech enhancement / denoise seam (VLS-004)

On the **local** STT tier, a noisy fan or reverberant room degrades transcription (cloud vendors denoise
server-side; the on-device path doesn't). Voxa ships the **seam**: `IAudioEnhancer` in `Voxa.Audio.Abstractions`
(`Enhance(pcm) → pcm`, same length/rate/channels), a `NullAudioEnhancer` passthrough, and an
`AudioEnhancerProcessor` placed **after the AEC stage and before the VAD** so detection and STT both see the
cleaned signal. Default (`Voxa:Enhance:Engine` unset / `None`) inserts **no** stage — byte-identical to today,
and zero cost. A real denoiser (the reference is DeepFilterNet3 on ONNX Runtime, in-process like Silero/Kokoro)
ships as a separate opt-in `Voxa.Audio.Enhance` follow-up; enabling it adds a real per-frame denoise pass whose
cost shows up directly in Studio's latency waterfall — you pay for it only when you turn it on.
