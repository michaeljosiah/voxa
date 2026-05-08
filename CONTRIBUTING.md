# Contributing to Voxa

Thanks for your interest. Voxa is pre-alpha and the public API is still moving — small focused PRs land fastest.

## Development setup

```bash
git clone https://github.com/michaeljosiah/voxa.git
cd voxa
dotnet restore
dotnet build
dotnet test
```

Voxa targets `net10.0`. You'll need the .NET 10 SDK.

## Repository layout

```
src/                          # All shipped libraries (one csproj per NuGet package)
  Voxa.Core/                  # Frames, processors, pipeline, runner — zero Azure deps
  Voxa.Testing/               # WAV processors, capturing helpers
  Voxa.Transports.WebSocket/  # Host-agnostic WebSocket source/sink
  Voxa.Services.AzureVoiceLive/
  Voxa.Services.AzureSpeech/
  Voxa.Services.MicrosoftAgents/
  Voxa.Observability/         # OpenTelemetry tracing helpers
tests/                        # One xUnit project per src project
samples/                      # Runnable sample apps
```

## Design principles

Keep these in mind when proposing changes:

1. **`Voxa.Core` has zero external dependencies** apart from NUlid. Don't add ASP.NET, OpenTelemetry, Azure, or any other framework references to it.
2. **Each processor is testable in isolation** with an in-memory transport or fake engine. If a processor needs an external service, abstract it behind an interface (see `IRealtimeApiTransport`, `ISpeechToTextEngine`).
3. **Backpressure matters.** Audio paths use `BoundedChannelFullMode.DropOldest`; control paths use `Wait`. Document any deviation.
4. **System frames preempt data frames.** Long-running data work must accept the per-frame `CancellationToken` so an interruption cancels it cleanly.
5. **Consuming processors forward unhandled frames.** If you add a new processor, make sure `StartFrame`/`EndFrame` propagate downstream so the sink can complete.

## Coding standards

- Records for frames and config (`with` for cheap clones).
- `internal sealed` until proven necessary to be public.
- One short doc comment line on every public surface — explain *why* not *what*.
- No emojis in code.
- `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` is on for all `src/` projects. Fix warnings, don't suppress them.

## Tests

- Every new processor needs a corresponding test project under `tests/`.
- Use the patterns in `Voxa.Testing` (CapturingProcessor + WaitForAsync) for timing-sensitive tests.
- Don't burn Azure quota in unit tests — script your engine/transport with an in-memory fake.
- Live integration tests against real Azure resources are welcome but should be gated by environment variables and skipped if unset.

## Pull requests

- Branch from `main`.
- Keep one logical change per PR.
- Update `CHANGELOG.md` under `[Unreleased]`.
- The CI workflow must pass before review.

## Releases

Maintainers tag a commit `v0.X.Y` and push the tag — the release workflow packs and publishes to NuGet.

## License

By contributing you agree your contributions will be licensed under the MIT license.
