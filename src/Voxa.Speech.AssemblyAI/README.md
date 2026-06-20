# Voxa.Speech.AssemblyAI

AssemblyAI **streaming** speech-to-text (Universal-Streaming v3) for the
[Voxa](https://github.com/michaeljosiah/voxa) pipeline.

Opens a WebSocket to `wss://streaming.assemblyai.com/v3/ws`, streams 16-bit PCM, and surfaces a live
turn transcript. Implements the same `ISpeechToTextEngine` contract as every Voxa STT provider (over
the shared `WebSocketSttEngine` base); pair it with the generic `SpeechToTextProcessor`.

## Use

```jsonc
{
  "Voxa": {
    "Stt": "AssemblyAI",
    "AssemblyAI": {
      "ApiKey": "...",
      "FormatTurns": true   // optional; punctuated/cased turns
    }
  }
}
```

`AddVoxa(configuration)` registers the descriptor automatically. À la carte:

```csharp
builder.Services.AddVoxa(builder.Configuration, voxa =>
    voxa.AddProvider(AssemblyAISpeechDescriptors.Stt));
```

| Key | Default | Notes |
|-----|---------|-------|
| `Voxa:AssemblyAI:ApiKey` | — | Required. Sent in the `Authorization` header. |
| `Voxa:AssemblyAI:FormatTurns` | `true` | Punctuated/cased turn transcripts. |
| `Voxa:AssemblyAI:InputSampleRate` | `16000` | PCM rate (`pcm_s16le`, mono). |

Like every Voxa streaming STT, interims drive the live transcript and a single final is emitted per
utterance at the VAD / smart-turn boundary — see `WebSocketSttEngine`. MIT licensed.
