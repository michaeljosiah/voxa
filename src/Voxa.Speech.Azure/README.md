# Voxa.Speech.Azure

Azure Speech Services STT and TTS engines for [Voxa](https://github.com/michaeljosiah/voxa) pipelines.

## Install

```bash
dotnet add package Voxa.Speech.Azure --prerelease
```

## Quickstart

```csharp
using Voxa.Speech;
using Voxa.Speech.Azure;

var speech = new AzureSpeechOptions
{
    SubscriptionKey = "<your-key>",
    Region = "northeurope",
    Voice = "en-US-JennyNeural",
};

var pipeline = Pipeline.Build()
    .Source(new WebSocketAudioSource(ws))
    .Then(AzureSpeech.StreamingTranscription(speech))     // or new SpeechToTextProcessor(new AzureSpeechToTextEngine(speech))
    .Then(new MicrosoftAgentsProcessor(yourAgent))
    .Then(AzureSpeech.Synthesis(speech))                  // or new TextToSpeechProcessor(new AzureTextToSpeechEngine(speech))
    .Sink(new WebSocketAudioSink(ws));
```

## What's included

- `AzureSpeechToTextEngine` — wraps `SpeechRecognizer` + `PushAudioInputStream` for streaming STT, surfaces interim and final transcripts.
- `AzureTextToSpeechEngine` — wraps `SpeechSynthesizer`, emits raw 24 kHz 16-bit mono PCM in 8 KB chunks.
- `AzureSpeechOptions` — subscription key, region, language, voice, sample rates.
- `AzureSpeech.StreamingTranscription(...)` / `AzureSpeech.Synthesis(...)` — one-liner processor factories.

## Migrating from Voxa.Services.AzureSpeech (v0.1.x)

The package was renamed in v0.2.0-alpha. The processor classes (`AzureSpeechSttProcessor`, `AzureSpeechTtsProcessor`) became the generic `SpeechToTextProcessor`/`TextToSpeechProcessor` in `Voxa.Speech.Abstractions`. The engines stay — just update the namespace from `Voxa.Services.AzureSpeech.Engines` to `Voxa.Speech.Azure`.

## License

MIT.
