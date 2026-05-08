# Voxa.Speech.ElevenLabs

ElevenLabs text-to-speech engine for [Voxa](https://github.com/michaeljosiah/voxa) pipelines. Best-in-class voice quality, voice cloning, and multilingual support.

## Install

```bash
dotnet add package Voxa.Speech.ElevenLabs --prerelease
```

## Quickstart

```csharp
using Voxa.Speech;
using Voxa.Speech.ElevenLabs;

var elevenlabs = new ElevenLabsOptions
{
    ApiKey = "<your-key>",
    VoiceId = "21m00Tcm4TlvDq8ikWAM",         // Rachel — pick from elevenlabs.io/voice-library
    ModelId = "eleven_multilingual_v2",
    OutputSampleRate = 24000,
};

var pipeline = Pipeline.Build()
    .Source(...)
    .Then(new MicrosoftAgentsProcessor(yourAgent))
    .Then(ElevenLabs.Synthesis(elevenlabs))
    .Sink(...);
```

## What's included

- `ElevenLabsTextToSpeechEngine` — POSTs to `/v1/text-to-speech/{voiceId}/stream` with `output_format=pcm_<rate>`, streams the chunked response.
- `ElevenLabsOptions` — api key, voice id, model id, output sample rate, optional voice settings (stability, similarity boost, style, speed, speaker boost).
- `ElevenLabs.Synthesis(...)` — one-liner processor factory.

This package is TTS-only; for STT pair with `Voxa.Speech.Azure`, `Voxa.Speech.OpenAI`, or `Voxa.Services.AzureVoiceLive`.

## License

MIT.
