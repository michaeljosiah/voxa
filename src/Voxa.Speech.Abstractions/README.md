# Voxa.Speech.Abstractions

Vendor-neutral STT and TTS abstractions for [Voxa](https://github.com/michaeljosiah/voxa) pipelines. Defines `ISpeechToTextEngine`, `ITextToSpeechEngine`, and the generic `SpeechToTextProcessor` / `TextToSpeechProcessor` that any vendor engine plugs into.

## Install

You typically don't install this package directly — install a vendor engine package (`Voxa.Speech.Azure`, `Voxa.Speech.OpenAI`, `Voxa.Speech.ElevenLabs`, `Voxa.Speech.Mistral`) and this package comes along as a transitive dependency.

If you're authoring a new vendor engine:

```bash
dotnet add package Voxa.Speech.Abstractions --prerelease
```

## What it provides

```csharp
public interface ISpeechToTextEngine : IAsyncDisposable
{
    Task StartAsync(CancellationToken ct);
    ValueTask WriteAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct);
    IAsyncEnumerable<TranscriptionResult> ReadTranscriptsAsync(CancellationToken ct);
    Task StopAsync();
}

public interface ITextToSpeechEngine : IAsyncDisposable
{
    Task StartAsync(CancellationToken ct);
    IAsyncEnumerable<byte[]> SynthesizeAsync(string text, CancellationToken ct);
}
```

Plus:
- `SpeechToTextProcessor` — pipes `AudioRawFrame` into the engine, emits `TranscriptionFrame`.
- `TextToSpeechProcessor` — drives the engine on `TextFrame`/`LlmTextChunkFrame`, emits `BotStartedSpeaking` + `AudioRawFrame` chunks + `BotStoppedSpeaking`.

## Usage

```csharp
using Voxa.Speech;

var pipeline = Pipeline.Build()
    .Source(new WebSocketAudioSource(ws))
    .Then(new SpeechToTextProcessor(new MyVendorSttEngine(opts)))
    .Then(new MicrosoftAgentsProcessor(agent))
    .Then(new TextToSpeechProcessor(new MyVendorTtsEngine(opts)))
    .Sink(new WebSocketAudioSink(ws));
```

## License

MIT.
