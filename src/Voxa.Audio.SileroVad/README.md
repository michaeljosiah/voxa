# Voxa.Audio.SileroVad

ML-based voice activity detection for [Voxa](https://github.com/michaeljosiah/voxa) pipelines using the Silero VAD v5 ONNX model. Drop-in replacement for `SilenceGateProcessor` when energy-only filtering isn't enough — handles keyboard noise, fans, distant chatter, music in the background. Same emission contract as Voice Live: `UserStartedSpeakingFrame` / `UserStoppedSpeakingFrame` on speech transitions.

## Install

```bash
dotnet add package Voxa.Audio.SileroVad --prerelease
```

The Silero VAD ONNX model (~2.3 MB, MIT-licensed) ships embedded in the assembly — no separate download or path config.

## Quickstart

Swap `SilenceGateProcessor` for `SileroVadProcessor`:

```csharp
using Voxa.Audio.SileroVad;
using Voxa.Speech;
using Voxa.Speech.Azure;

var pipeline = Pipeline.Build()
    .Source(new WebSocketAudioSource(ws, new WebSocketAudioOptions { InputSampleRate = 16000 }))
    .Then(new SileroVadProcessor())                            // ← replaces SilenceGateProcessor
    .Then(AzureSpeech.StreamingTranscription(azureSpeech))
    .Then(new MicrosoftAgentsProcessor(yourAgent))
    .Then(AzureSpeech.Synthesis(azureSpeech))
    .Sink(new WebSocketAudioSink(ws));
```

## Tuning

```csharp
new SileroVadProcessor(new SileroVadOptions
{
    SampleRate = 16000,             // also supports 8000
    ActivationThreshold = 0.5f,     // probability to open gate
    DeactivationThreshold = 0.35f,  // probability to close gate (hysteresis)
    MinSpeechWindows = 2,           // ~64 ms sustained → speech-start
    MinSilenceWindows = 8,          // ~256 ms sustained → speech-end
})
```

## How it compares to SilenceGateProcessor

| | `SilenceGateProcessor` | `SileroVadProcessor` |
|---|---|---|
| Pkg size | ~10 KB | ~2.3 MB model + ~50 MB ONNX runtime native binaries |
| Detects | RMS amplitude | Trained speech/non-speech classifier |
| Catches keyboard / fan / chair scrape | no — high RMS, gets through | yes — classified non-speech |
| Catches distant or quiet speech below RMS threshold | no — gets dropped | yes — classified speech |
| Threshold tuning | per-room | usually unneeded |
| Per-frame cost | ~10 µs | ~2–3 ms on CPU (still ~50× real-time at 16 kHz) |
| Cold start | instant | ~30 ms (embedded model load) |

Use `SilenceGateProcessor` for the simplest case (clean office). Use `SileroVadProcessor` for production / noisy environments / mobile clients.

## Constraints

- Sample rate must be **16000** or **8000** Hz. Silero v5 doesn't support arbitrary rates. Audio at other rates is forwarded untouched with a warning — resample upstream if you need it.
- Skip on the Voice Live composite path. Voice Live includes its own server-side VAD; running Silero on top is wasted work.

## License

MIT. The bundled Silero VAD model is MIT-licensed by [snakers4/silero-vad](https://github.com/snakers4/silero-vad).
