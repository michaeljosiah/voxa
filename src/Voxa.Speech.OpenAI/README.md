# Voxa.Speech.OpenAI

OpenAI Whisper STT and OpenAI TTS engines for [Voxa](https://github.com/michaeljosiah/voxa) pipelines.

## Install

```bash
dotnet add package Voxa.Speech.OpenAI --prerelease
```

## Quickstart

```csharp
using Voxa.Speech;
using Voxa.Speech.OpenAI;

var openai = new OpenAISpeechOptions
{
    ApiKey = "<your-key>",
    TtsModel = "gpt-4o-mini-tts",
    TtsVoice = "alloy",
    SttModel = "whisper-1",
    SttBufferSeconds = 3,
};

var pipeline = Pipeline.Build()
    .Source(new WebSocketAudioSource(ws))
    .Then(OpenAISpeech.StreamingTranscription(openai))
    .Then(new MicrosoftAgentsProcessor(yourAgent))
    .Then(OpenAISpeech.Synthesis(openai))
    .Sink(new WebSocketAudioSink(ws));
```

## What's included

- `OpenAIWhisperEngine` — buffers PCM audio for `SttBufferSeconds`, wraps as WAV, posts to `/v1/audio/transcriptions`. Emits one final `TranscriptionResult` per batch. For ultra-low-latency streaming, use the Realtime API path (`Voxa.Services.AzureVoiceLive` pointed at an OpenAI Realtime endpoint).
- `OpenAITextToSpeechEngine` — POSTs to `/v1/audio/speech` with `response_format=pcm`, streams the response body in 8 KB chunks.
- `OpenAISpeechOptions` — endpoint URL (configurable for OpenAI-compatible proxies), model, voice, language.
- `OpenAISpeech.StreamingTranscription(...)` / `OpenAISpeech.Synthesis(...)` — one-liner processor factories.

## License

MIT.
