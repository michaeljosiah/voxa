# Voxa.Core

Core pipeline primitives for the [Voxa](https://github.com/michaeljosiah/voxa) real-time voice AI framework: `Frame`, `FrameProcessor`, `Pipeline`, `PipelineRunner`.

## Install

```bash
dotnet add package Voxa.Core --prerelease
```

## Quickstart

```csharp
using Voxa.Frames;
using Voxa.Pipelines;
using Voxa.Processors;

var pipeline = Pipeline.Build()
    .Source(new PipelineSource())
    .Then(new MyProcessor())
    .Sink(new PipelineSink());

await using var runner = new PipelineRunner(pipeline);
await runner.StartAsync();
await pipeline.Source.IngestAsync(new TextFrame("hello"));
await runner.WaitAsync();
```

## What's included

- 14 frame types under `Voxa.Frames` — audio, transcription, text, tool calls, control + system
- `FrameProcessor` base class with two-task drain — system frames (interruption, errors) preempt data frames via cancellation
- `Pipeline`, `PipelineBuilder`, `PipelineRunner` — fluent composition + lifecycle owner
- `VoxaActivities.Source` — public `ActivitySource` named `"Voxa"` for OpenTelemetry

Zero dependencies beyond NUlid.

## Status

Pre-alpha. See the [main README](https://github.com/michaeljosiah/voxa) for the full architecture overview, package matrix, and roadmap.

## License

MIT.
