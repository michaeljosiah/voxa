# Voxa.Services.AzureSpeech

Granular Azure Speech STT and TTS processors for [Voxa](https://github.com/michaeljosiah/voxa). Use this as a regional fallback when Voice Live isn't available, or when you need finer control over the STT/LLM/TTS chain.

## Install

```bash
dotnet add package Voxa.Services.AzureSpeech --prerelease
```

## Quickstart

A typical granular pipeline pairs this package with `Voxa.Services.MicrosoftAgents`:

```csharp
var speechOpts = new AzureSpeechOptions
{
    SubscriptionKey = "<your-key>",
    Region = "northeurope",
    Voice = "en-US-JennyNeural",
};

var pipeline = Pipeline.Build()
    .Source(new WebSocketAudioSource(ws))
    .Then(new AzureSpeechSttProcessor(speechOpts))
    .Then(new MicrosoftAgentsProcessor(yourAgent))
    .Then(new AzureSpeechTtsProcessor(speechOpts))
    .Sink(new WebSocketAudioSink(ws));
```

## What's included

- `AzureSpeechSttProcessor` — pipes `AudioRawFrame` into the SDK's `SpeechRecognizer` (continuous recognition), emits interim + final `TranscriptionFrame`.
- `AzureSpeechTtsProcessor` — synthesises `TextFrame`/`LlmTextChunkFrame` into 24 kHz PCM `AudioRawFrame` chunks with `BotStartedSpeaking`/`BotStoppedSpeaking` bookends.
- `ISpeechToTextEngine`, `ITextToSpeechEngine` — engine abstractions for testing without burning Azure quota. Default impls (`AzureSpeechToTextEngine`, `AzureTextToSpeechEngine`) wrap the Cognitive Services Speech SDK.

## License

MIT.
