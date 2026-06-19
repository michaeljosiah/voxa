# Voxa.TurnTaking — turn-taking quality benchmark (VRT-001)

A harness that drives the **real composed pipeline** (`DefaultVoicePipelineComposer.Compose`) through a
turn-taking corpus and turns *"did this change actually improve the conversation?"* into a per-category
number a regression gate can guard. It walks the
[Full-Duplex-Bench v1.0](https://github.com/DanielLin94144/Full-Duplex-Bench) layout, runs each sample,
and reduces the existing `VoxaDiagnosticsHub` stage timings to a per-sample JSON record — it adds **no new
instrumentation** and builds **no parallel pipeline**, so the numbers equal what production reports.

This is the measurement foundation the rest of the VRT line (eager STT, barge-in/AEC, streaming captions)
is gated against. See [`docs/specifications/vrt-001-turn-taking-benchmark-spec.html`](../../docs/specifications/vrt-001-turn-taking-benchmark-spec.html).

## Run it

```bash
# Offline smoke — mock engines, the bundled mini fixture (what CI gates on):
dotnet run -c Release --project bench/Voxa.TurnTaking

# Same, with the regression gate against the checked-in baseline (non-zero exit on regression):
dotnet run -c Release --project bench/Voxa.TurnTaking -- --baseline bench/Voxa.TurnTaking/baseline.json

# One category, real local engines, a fetched full corpus (LocalModels lane):
dotnet run -c Release --project bench/Voxa.TurnTaking -- \
  --corpus-dir ./full-duplex-bench --category smooth_turn_taking \
  --stt WhisperCpp --tts Kokoro --llm openai --out-dir ./fdb-full
```

A run writes, into `--out-dir`: a per-sample `<category>__<id>.json` + a `.response.wav`, a per-category
`summary.csv` (p50/p90/p99), and a `score.json` (the direction-aware per-category score).

| Flag | Default | Meaning |
|---|---|---|
| `--corpus-dir <path>` | the bundled mini fixture | Root of the FDB-layout corpus (`<category>/<id>/…`). |
| `--out-dir <path>` | `./fdb-out` | Where per-sample JSON, response WAVs, `summary.csv`, `score.json` land. |
| `--category <name>` | all three | `pause_handling` \| `smooth_turn_taking` \| `user_interruption`. `backchannel` is always skipped. |
| `--limit <n>` | (none) | Cap samples per category. |
| `--stt` / `--tts` / `--llm` | `mock` / `mock` / `Echo` | Provider names passed to `AddVoxa`. Mock is the default so the zero-arg run is offline. |
| `--baseline <path>` | (none) | Diff the score against a checked-in baseline within tolerance; non-zero exit on regression. |
| `--make-mini-fixture <wav>` | — | Regenerate the checked-in mini fixture from a real source WAV (`--dest <dir>`). |
| `--write-baseline <path>` | — | Run, then write the score out as a baseline (a deliberate, reviewed refresh). |

**Mock defaults are load-bearing:** the zero-argument run uses deterministic mock STT/TTS + the keyless
Echo agent over the bundled mini fixture, so it touches no network and downloads nothing — the offline
smoke path is the path of least resistance.

## The three cascade-fair categories + scoring

A cascade (STT→LLM→TTS, half-duplex) can be scored fairly on three of FDB's four behaviours. Direction
matters — the same raw quantity is good in one category and bad in another:

| Category | Metric | Better is |
|---|---|---|
| `pause_handling` | turn-offset-rate (fraction of samples where the turn ended *during* the within-turn pause) | **lower** |
| `smooth_turn_taking` | first-word latency after end-of-turn (the ttfb p50) | **higher responsiveness** (lower ms) |
| `user_interruption` | barge-in **yield** latency (how fast the bot stops when interrupted) | **higher responsiveness** (lower ms) |
| `backchannel` | — | **skipped** — needs a full-duplex model a cascade can't be; discovered, logged skipped, never scored (§5) |

The TOR is detected from the diagnostics turn edges: more than one `UserStopped` on a `pause_handling`
sample means the VAD ended the turn during the within-turn pause — a premature turn-take. Refreshing
`baseline.json` is a deliberate, reviewed change (a knob moved, a category improved), never a silent
overwrite — the same discipline as bumping a model SHA-pin.

> **Barge-in note.** `user_interruption` scores the bot's **yield** (a `UserStarted` while the bot is
> speaking → its stop/interrupt edge), *not* how fast it replies after the user stops — so a system that
> talks over the interruption can't score well, and barge-in/AEC regressions stay visible. A real barge-in
> needs the bot speaking *while* the user audio arrives; the offline file-driven source has no real-time
> overlap and produces none, so offline the yield is **reported as not-exercised** (null, not gated). It
> populates on a real-time / full-duplex source — the metric is wired and correct, the offline lane just
> can't exercise it.

## The mini fixture

`fixtures/fdb-mini/` is a tiny, **real** corpus checked in for the offline smoke gate — derived from the
repo's `jfk.wav` (a public-domain JFK address), silence-padded to shape each turn (each sample's
`meta.json` records the provenance). It exercises the *plumbing*; the behavioural numbers come from the
full FDB corpus. Regenerate it with `--make-mini-fixture <source.wav>`.

## Full-corpus fetch (the real numbers)

Full-Duplex-Bench v1.0 ([arXiv 2503.04721](https://arxiv.org/abs/2503.04721), ASRU 2025) ships via
[Google Drive](https://github.com/DanielLin94144/Full-Duplex-Bench) (its license governs redistribution),
not a single tarball — so it isn't vendored here. To run the real numbers:

1. Fetch the corpus from the project's Drive link.
2. Lay it out as `<corpus>/<category>/<sample-id>/input.wav` (+ optional `meta.json` with a `reference`).
3. Point `--corpus-dir` at it with real engines and a real agent, e.g.
   `--corpus-dir ./full-duplex-bench --stt WhisperCpp --tts Kokoro --llm openai`.

The default lane never needs the full corpus; it's a `LocalModels`/manual-lane concern.

## Status — complete (Phases A–D)

- **A** — driver spine: corpus walk, per-sample run over `Compose`, per-sample JSON + response WAV, mini fixture.
- **B** — real local engines via `--stt/--tts/--llm` (a `LocalModels`-gated test runs WhisperCpp + Kokoro).
- **C** — `summary.csv`: per-category p50/p90/p99, error counts, backchannel logged-skipped.
- **D** — direction-aware `score.json` + the checked-in `baseline.json` regression gate (an xUnit smoke test
  diffs a mock run against it within tolerance, so a turn-taking regression fails the build).

A VRT-002/003/004 PR cites a before/after delta from this harness as its evidence.
