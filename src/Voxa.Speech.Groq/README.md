# Voxa.Speech.Groq

Groq Whisper speech-to-text for the [Voxa](https://github.com/michaeljosiah/voxa) pipeline.

Groq serves Whisper (`whisper-large-v3-turbo` by default) on an OpenAI-compatible
`/audio/transcriptions` API, so this package is a thin descriptor over Voxa's proven
`OpenAIWhisperEngine` — pointed at Groq's base URL. It implements the same
`ISpeechToTextEngine` contract every Voxa STT provider does; pair it with the generic
`SpeechToTextProcessor` from `Voxa.Speech.Abstractions`.

## Use

```jsonc
{
  "Voxa": {
    "Stt": "Groq",
    "Groq": {
      "ApiKey": "gsk_...",
      "SttModel": "whisper-large-v3-turbo" // optional; also whisper-large-v3
    }
  }
}
```

`AddVoxa(configuration)` registers the Groq descriptor automatically. À la carte:

```csharp
builder.Services.AddVoxa(builder.Configuration, voxa =>
    voxa.AddProvider(GroqSpeechDescriptors.Stt));
```

| Key | Default | Notes |
|-----|---------|-------|
| `Voxa:Groq:ApiKey` | — | Required. Sent as `Authorization: Bearer`. |
| `Voxa:Groq:SttModel` | `whisper-large-v3-turbo` | Any Groq Whisper model. |
| `Voxa:Groq:ApiBaseUrl` | `https://api.groq.com/openai/v1` | Override for a proxy. |
| `Voxa:Groq:SttLanguage` | — | Optional BCP-47 hint; omit to auto-detect. |

REST (batch) transcription: audio is buffered per utterance and posted at speech-end (VAD-driven),
matching the OpenAI Whisper provider. For ultra-low-latency streaming, use a streaming provider
(Deepgram, AssemblyAI, …).

MIT licensed.
