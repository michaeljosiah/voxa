# Voxa.Speech.Mistral

Mistral Voxtral-TTS engine for [Voxa](https://github.com/michaeljosiah/voxa) pipelines. Mistral's audio API is OpenAI-compatible at `/v1/audio/speech`.

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
- `MistralSpeechOptions` — api key, base URL, model, voice, output sample rate.
- `Mistral.Synthesis(...)` — one-liner processor factory.

This package is TTS-only. For STT pair with another vendor (`Voxa.Speech.Azure`, `Voxa.Speech.OpenAI`).

## License

MIT.
