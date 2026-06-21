# Voxa.Speech.Google

Google Cloud **Speech-to-Text v2** streaming speech-to-text for the
[Voxa](https://github.com/michaeljosiah/voxa) pipeline.

Uses the official `Google.Cloud.Speech.V2` gRPC client: opens a `StreamingRecognize` call, streams
LINEAR16 audio, and surfaces interim + final results. Implements the same `ISpeechToTextEngine`
contract as every Voxa STT provider; pair it with the generic `SpeechToTextProcessor`.

## Use

```jsonc
{
  "Voxa": {
    "Stt": "Google",
    "Google": {
      "ProjectId": "my-gcp-project",
      "Language": "en-US",
      "Model": "long",
      "CredentialsPath": "C:/keys/sa.json" // or CredentialsJson, or ADC (GOOGLE_APPLICATION_CREDENTIALS)
    }
  }
}
```

`AddVoxa(configuration)` registers the descriptor automatically. À la carte:

```csharp
builder.Services.AddVoxa(builder.Configuration, voxa =>
    voxa.AddProvider(GoogleSpeechDescriptors.Stt));
```

| Key | Default | Notes |
|-----|---------|-------|
| `Voxa:Google:ProjectId` | — | Required (GCP project). |
| `Voxa:Google:Location` | `global` | A region (e.g. `us-central1`) sets the regional endpoint. |
| `Voxa:Google:Language` | `en-US` | BCP-47 code. |
| `Voxa:Google:Model` | `long` | `long` / `short` / `telephony` / … |
| `Voxa:Google:CredentialsPath` / `CredentialsJson` | — | Service-account key; omit to use ADC. |

Interims drive the live transcript; one final per utterance is emitted at the VAD / smart-turn
boundary (shared `StreamingTranscriptAccumulator`). MIT licensed.
