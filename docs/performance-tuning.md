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
| `SentenceAggregator.EagerFirstChunkMinChars` | 0 (off) | On the **first** flush of a turn, also break at a clause boundary (`, ; :`) once this many chars are buffered — gets the opening audio out 100–400 ms sooner. Later flushes use full sentence boundaries. | Slightly more, shorter first chunk. 40 is a good starting value (used by the sample server). |
| `SentenceAggregator.MaxBufferChars` | 500 | Hard cap that forces a flush even without a boundary, so TTS never stalls on a runaway response. | — |

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
