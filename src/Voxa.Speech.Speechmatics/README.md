# Voxa.Speech.Speechmatics

Speechmatics **streaming** speech-to-text (real-time v2) for the
[Voxa](https://github.com/michaeljosiah/voxa) pipeline.

Connects to the Speechmatics RT WebSocket, sends a `StartRecognition` handshake, streams 16-bit PCM,
and surfaces partial + final transcripts. Implements the same `ISpeechToTextEngine` contract as every
Voxa STT provider (over the shared `WebSocketSttEngine` base); pair it with the generic
`SpeechToTextProcessor`.

## Use

```jsonc
{
  "Voxa": {
    "Stt": "Speechmatics",
    "Speechmatics": {
      "ApiKey": "...",
      "Language": "en"
    }
  }
}
```

`AddVoxa(configuration)` registers the descriptor automatically. À la carte:

```csharp
builder.Services.AddVoxa(builder.Configuration, voxa =>
    voxa.AddProvider(SpeechmaticsSpeechDescriptors.Stt));
```

| Key | Default | Notes |
|-----|---------|-------|
| `Voxa:Speechmatics:ApiKey` | — | Required. Sent as `Authorization: Bearer`. |
| `Voxa:Speechmatics:Language` | `en` | Transcription language. |
| `Voxa:Speechmatics:ApiBaseUrl` | `wss://eu2.rt.speechmatics.com/v2` | Region / on-prem override. |

Partials drive the live transcript; one final per utterance is emitted at the VAD / smart-turn
boundary — see `WebSocketSttEngine`. MIT licensed.
