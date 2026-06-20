# Voxa.Speech.Deepgram

Deepgram **streaming** speech-to-text for the [Voxa](https://github.com/michaeljosiah/voxa) pipeline.

Opens a WebSocket to `wss://api.deepgram.com/v1/listen`, streams 16-bit PCM, and surfaces a live
interim transcript while the user speaks. It implements the same `ISpeechToTextEngine` contract as
every Voxa STT provider (over the shared `WebSocketSttEngine` base); pair it with the generic
`SpeechToTextProcessor`.

## Use

```jsonc
{
  "Voxa": {
    "Stt": "Deepgram",
    "Deepgram": {
      "ApiKey": "...",
      "Model": "nova-3" // optional; also nova-2, nova-2-phonecall, …
    }
  }
}
```

`AddVoxa(configuration)` registers the Deepgram descriptor automatically. À la carte:

```csharp
builder.Services.AddVoxa(builder.Configuration, voxa =>
    voxa.AddProvider(DeepgramSpeechDescriptors.Stt));
```

| Key | Default | Notes |
|-----|---------|-------|
| `Voxa:Deepgram:ApiKey` | — | Required. Sent as `Authorization: Token`. |
| `Voxa:Deepgram:Model` | `nova-3` | Any Deepgram model. |
| `Voxa:Deepgram:Language` | — | Optional BCP-47 hint. |
| `Voxa:Deepgram:ApiBaseUrl` | `wss://api.deepgram.com/v1/listen` | Override for a proxy/on-prem. |

## Turn integration

Deepgram emits many `is_final` segments per utterance, but Voxa fires one agent turn per final
transcription. So this engine streams **interims** for live display, **accumulates** the finalized
segments, and emits **one** final when the VAD / smart-turn detector ends the turn (`FlushAsync()`).
You get streaming's latency (no post-speech round-trip) with exactly one turn per utterance, and
Voxa's VAD / smart-turn stays authoritative across every STT vendor.

MIT licensed.
