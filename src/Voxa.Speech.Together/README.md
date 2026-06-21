# Voxa.Speech.Together

Together AI Whisper speech-to-text for the [Voxa](https://github.com/michaeljosiah/voxa) pipeline.

Together serves Whisper (`openai/whisper-large-v3`) on an OpenAI-compatible
`/audio/transcriptions` API, so this package is a thin descriptor over Voxa's proven
`OpenAIWhisperEngine` pointed at Together's base URL. Same `ISpeechToTextEngine` contract as
every Voxa STT provider; pair it with the generic `SpeechToTextProcessor`.

## Use

```jsonc
{
  "Voxa": {
    "Stt": "Together",
    "Together": {
      "ApiKey": "...",
      "SttModel": "openai/whisper-large-v3" // optional
    }
  }
}
```

`AddVoxa(configuration)` registers the Together descriptor automatically. À la carte:

```csharp
builder.Services.AddVoxa(builder.Configuration, voxa =>
    voxa.AddProvider(TogetherSpeechDescriptors.Stt));
```

| Key | Default | Notes |
|-----|---------|-------|
| `Voxa:Together:ApiKey` | — | Required. Sent as `Authorization: Bearer`. |
| `Voxa:Together:SttModel` | `openai/whisper-large-v3` | Together Whisper model id. |
| `Voxa:Together:ApiBaseUrl` | `https://api.together.xyz/v1` | Override for a proxy. |
| `Voxa:Together:SttLanguage` | — | Optional BCP-47 hint; omit to auto-detect. |

REST (batch) transcription, posted at speech-end (VAD-driven), like the OpenAI Whisper provider.

MIT licensed.
