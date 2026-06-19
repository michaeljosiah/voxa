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

# One category, real local engines, a fetched full corpus (LocalModels lane):
dotnet run -c Release --project bench/Voxa.TurnTaking -- \
  --corpus-dir ./full-duplex-bench --category smooth_turn_taking \
  --stt WhisperCpp --tts Kokoro --llm openai --out-dir ./fdb-full
```

| Flag | Default | Meaning |
|---|---|---|
| `--corpus-dir <path>` | the bundled mini fixture | Root of the FDB-layout corpus (`<category>/<id>/…`). |
| `--out-dir <path>` | `./fdb-out` | Where per-sample JSON + response WAVs land. |
| `--category <name>` | all three | `pause_handling` \| `smooth_turn_taking` \| `user_interruption`. `backchannel` is always skipped. |
| `--limit <n>` | (none) | Cap samples per category. |
| `--stt` / `--tts` / `--llm` | `mock` / `mock` / `Echo` | Provider names passed to `AddVoxa`. Mock is the default so the zero-arg run is offline. |

**Mock defaults are load-bearing:** the zero-argument run uses deterministic mock STT/TTS + the keyless
Echo agent over the bundled mini fixture, so it touches no network and downloads nothing — the offline
smoke path is the path of least resistance.

## The three cascade-fair categories

A cascade (STT→LLM→TTS, half-duplex) can be scored fairly on three of FDB's four behaviours. The fourth,
`backchannel`, needs a full-duplex model that talks and listens at once; the harness **discovers it, logs
it skipped, and emits no score** — never a fabricated number (VRT-001 §5).

## The mini fixture

`fixtures/fdb-mini/` is a tiny, **real** corpus checked in for the offline smoke gate — derived from the
repo's `jfk.wav` (a public-domain JFK address), silence-padded to shape each turn (each sample's
`meta.json` records the provenance). It exercises the *plumbing*; the behavioural numbers come from the
full FDB corpus. Regenerate it with `--make-mini-fixture <source.wav>`.

> **Full-corpus fetch** (the real numbers) is wired in Phase B — Full-Duplex-Bench ships via Google Drive,
> not a single tarball; the fetch + on-disk layout will be documented here alongside the local-engine lane.

## Status (Phase A)

The mock-driver spine: corpus walk, per-sample run over `Compose`, per-sample JSON + response WAV, the
mini fixture, and the offline smoke test. Real local engines (Phase B), the summary roll-up (Phase C), and
the direction-aware scorer + `baseline.json` regression gate (Phase D) follow as separate PRs.
