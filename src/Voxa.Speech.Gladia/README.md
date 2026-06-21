# Voxa.Speech.Gladia

Gladia **streaming** speech-to-text (live v2) for the [Voxa](https://github.com/michaeljosiah/voxa) pipeline.

Gladia is two-step: an HTTP `POST /v2/live` (with your key) returns a pre-signed WebSocket URL, then
audio streams over that socket. The engine handles both on the shared `WebSocketSttEngine` base; pair
it with the generic `SpeechToTextProcessor`.

## Use

```jsonc
{
  "Voxa": {
    "Stt": "Gladia",
    "Gladia": {
      "ApiKey": "...",
      "Language": "en" // optional; omit to auto-detect
    }
  }
}
```

`AddVoxa(configuration)` registers the descriptor automatically. À la carte:

```csharp
builder.Services.AddVoxa(builder.Configuration, voxa =>
    voxa.AddProvider(GladiaSpeechDescriptors.Stt));
```

| Key | Default | Notes |
|-----|---------|-------|
| `Voxa:Gladia:ApiKey` | — | Required. Sent as the `x-gladia-key` header. |
| `Voxa:Gladia:Language` | — | Optional BCP-47 hint. |
| `Voxa:Gladia:InputSampleRate` | `16000` | PCM rate (16-bit, mono). |

Interims drive the live transcript; one final per utterance is emitted at the VAD / smart-turn
boundary — see `WebSocketSttEngine`. MIT licensed.
