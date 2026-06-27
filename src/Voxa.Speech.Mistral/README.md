# Voxa.Speech.Mistral

Mistral Voxtral **TTS and STT** engines for [Voxa](https://github.com/michaeljosiah/voxa) pipelines. Mistral's audio API is OpenAI-compatible: TTS at `/v1/audio/speech`, transcription at `/v1/audio/transcriptions`.

This is the **remote** (API, key-required) Voxtral tier. For a **local**, fully-offline privacy tier on the same model family, use `Voxa.Speech.Voxtral` (`Voxtral-Mini-4B-Realtime` via local vLLM) and switch with `"Voxa:Stt": "Voxtral"` vs `"Mistral"`.

## Install

```bash
dotnet add package Voxa.Speech.Mistral --prerelease
```

## Quickstart

```csharp
using Voxa.Speech;
using Voxa.Speech.Mistral;

var mistral = new MistralSpeechOptions
{
    ApiKey = "<your-mistral-key>",
    Model = "voxtral-tts",
    Voice = "alloy",
};

var pipeline = Pipeline.Build()
    .Source(...)
    .Then(new MicrosoftAgentsProcessor(yourAgent))
    .Then(Mistral.Synthesis(mistral))
    .Sink(...);
```

## What's included

- `MistralTextToSpeechEngine` — POSTs to `/v1/audio/speech` with `response_format=pcm`, streams the chunked response.
- `MistralSpeechToTextEngine` — buffers the utterance and POSTs it to `/v1/audio/transcriptions` at speech-end.
  With `SttStreaming` (default **true**) it sends `stream=true` and surfaces the SSE response as interim
  `transcription.text.delta` partials followed by a `transcription.done` final; set it false for a single
  batched final. The API takes a complete clip, so audio is sent once per utterance (not incrementally).
- `MistralVoiceCatalog` — list/clone voices via `/v1/audio/voices`.
- `MistralSpeechOptions` — api key, base URL, model/voice, STT model/language/streaming, sample rates.
- `Mistral.Synthesis(...)` — one-liner TTS processor factory.

Register both via the meta-package and pick per role: `"Voxa:Tts": "Mistral"` and/or `"Voxa:Stt": "Mistral"`.

### STT config keys

| Key | Default | Notes |
|-----|---------|-------|
| `Voxa:Mistral:SttModel` | `voxtral-mini-latest` | Transcription model id. |
| `Voxa:Mistral:SttStreaming` | `true` | Stream the SSE response (interims + final); false = one batched final. |
| `Voxa:Mistral:SttLanguage` | `null` | BCP-47 hint; null auto-detects. |
| `Voxa:Mistral:InputSampleRate` | `16000` | PCM rate the engine wraps as WAV before posting. |
| `Voxa:Mistral:SttBufferSeconds` | `30` | Safety-backstop flush if VAD never fires speech-end; 0 disables. |

## License

MIT.
