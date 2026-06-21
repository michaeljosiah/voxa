# Voxa.Speech.Aws

AWS Transcribe **streaming** speech-to-text for the [Voxa](https://github.com/michaeljosiah/voxa) pipeline.

Uses the official `AWSSDK.TranscribeStreaming` (v4) client: feeds PCM audio events into a Transcribe
stream and surfaces partial + final results. Implements the same `ISpeechToTextEngine` contract as
every Voxa STT provider; pair it with the generic `SpeechToTextProcessor`.

## Use

```jsonc
{
  "Voxa": {
    "Stt": "Aws",
    "Aws": {
      "Region": "us-east-1",
      "Language": "en-US"
      // AccessKeyId/SecretAccessKey optional — omit to use the default AWS credential chain
      // (environment, shared profile, or IAM role).
    }
  }
}
```

`AddVoxa(configuration)` registers the descriptor automatically. À la carte:

```csharp
builder.Services.AddVoxa(builder.Configuration, voxa =>
    voxa.AddProvider(AwsSpeechDescriptors.Stt));
```

| Key | Default | Notes |
|-----|---------|-------|
| `Voxa:Aws:Region` | `us-east-1` | AWS region system name. |
| `Voxa:Aws:Language` | `en-US` | AWS Transcribe language code. |
| `Voxa:Aws:AccessKeyId` / `SecretAccessKey` | — | Set together, or omit for the default credential chain. |
| `Voxa:Aws:InputSampleRate` | `16000` | PCM rate (mono). |

Partials drive the live transcript; one final per utterance is emitted at the VAD / smart-turn
boundary (shared `StreamingTranscriptAccumulator`). MIT licensed.
