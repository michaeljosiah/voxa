# Voxa.Observability

OpenTelemetry tracing helpers for [Voxa](https://github.com/michaeljosiah/voxa) pipelines.

## Install

```bash
dotnet add package Voxa.Observability --prerelease
```

## Quickstart

Drop `TracingProcessor`s anywhere in the pipeline you want a probe:

```csharp
var pipeline = Pipeline.Build()
    .Source(...)
    .Then(new TracingProcessor("user-input"))
    .Then(new AzureVoiceLiveProcessor(opts))
    .Then(new TracingProcessor("voice-live-out"))
    .Sink(...);
```

Wire OpenTelemetry to the public `Voxa` ActivitySource:

```csharp
services.AddOpenTelemetry()
    .WithTracing(t => t
        .AddSource(VoxaActivities.SourceName)   // "Voxa"
        .AddOtlpExporter());
```

## Per-frame tags

Every emitted span carries:

- `voxa.scope` — the `TracingProcessor` label
- `voxa.frame.id` — ULID
- `voxa.frame.type` — concrete frame class name
- `voxa.frame.direction` — Downstream | Upstream

Plus payload-aware tags:
- `AudioRawFrame` → sample_rate, channels, bytes
- `TranscriptionFrame` → is_final, text_length
- `ToolCallRequestFrame` → name, call_id
- `ErrorFrame` → activity status set to Error with the message

## License

MIT.
