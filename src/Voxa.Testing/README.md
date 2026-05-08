# Voxa.Testing

Test helpers for [Voxa](https://github.com/michaeljosiah/voxa) pipelines: WAV file source/sink, capturing processors, passthrough.

## Install

```bash
dotnet add package Voxa.Testing --prerelease
```

## Quickstart

End-to-end fixture-based test — read a WAV file, run it through your pipeline, write the result back to disk:

```csharp
using Voxa.Pipelines;
using Voxa.Testing.Audio;
using Voxa.Testing.Processors;

var pipeline = Pipeline.Build()
    .Source(new WavFileSourceProcessor("input.wav", frameDurationMs: 20))
    .Then(myProcessor)
    .Sink(new WavFileSinkProcessor("output.wav"));

await using var runner = new PipelineRunner(pipeline);
await runner.StartAsync();
await runner.WaitAsync();

var result = WavFile.Read("output.wav");
```

Or capture frames mid-pipeline for assertion:

```csharp
var captured = new CapturingProcessor();
// ... insert in pipeline ...
await captured.WaitForAsync(expected: 3, TimeSpan.FromSeconds(2));
Assert.Contains(captured.Captured, f => f is TextFrame);
```

## What's included

- `WavFile` — RIFF/WAV codec for 16-bit PCM (read/write/parse)
- `WavFileSourceProcessor`, `WavFileSinkProcessor`
- `CapturingProcessor` — records every frame with a polling `WaitForAsync` helper
- `PassthroughProcessor` — forwards everything unchanged

## License

MIT.
