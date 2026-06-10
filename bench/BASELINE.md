# Voxa performance baseline (VPS-001)

BenchmarkDotNet results captured while implementing the [performance optimization
spec](../docs/specifications/voxa-performance-optimization-spec.html). Each workstream appends its
before/after numbers here.

## How to run

```
# full run (minutes per benchmark, authoritative):
dotnet run -c Release --project bench/Voxa.Benchmarks -- --filter *

# quick indicative run (ShortRun, N=3 — used for the tables below):
dotnet run -c Release --project bench/Voxa.Benchmarks -- --filter * --job short

# list:
dotnet run -c Release --project bench/Voxa.Benchmarks -- --list flat
```

> The tables below are **ShortRun (N=3)** captures on the development machine (Windows 11, .NET 10.0.201).
> Means carry ~5–10% error at this sample count; the **Allocated** column is exact and is the primary
> signal the spec targets. Re-run with a full job for publishable timing numbers.

---

## WS5 — Silero VAD inference (`VadBenchmarks.Probability`)

One inference = one 512-sample window at 16 kHz (the pipeline runs ~31/sec/session).

| Stage | Allocated/op | Notes |
|---|---|---|
| Original (`new DenseTensor` input + `NamedOnnxValue[]` + `ToDenseTensor()` state clone + `Run` result collection) | ~5 KB (analytical) | per-inference input tensors + state clone + result collection |
| Intermediate (reuse input/state tensors only) | **1.72 KB** (measured) | input side removed; `Run` still returns a result collection holding ORT-owned output tensors |
| **After WS5** (pre-bound `OrtValue` inputs *and* outputs via `Run(RunOptions, names, values, names, values)`) | **272 B** (measured) | ~18× fewer bytes than original; residual is ORT P/Invoke marshaling of the input/output name arrays — not removable without unsafe interop |

Mean: ~134 µs/op (dominated by the ONNX graph itself, unchanged by the allocation work).

Correctness guarded by `SileroVadDeterminismTests` (identical probability sequence across a `Reset`,
±1e-6) plus the existing engine tests.

---

## WS3 — Wire serialization (`WireProtocolBenchmarks`) — BEFORE

Captured **before** WS3 (reflection-based `JsonSerializer.Serialize(anonymousObject)` returning a
`string`; note the later `Encoding.UTF8.GetBytes` in the sink is *not* included here, so the real
per-envelope cost was higher):

| Method | Mean | Allocated |
|---|---|---|
| BuildTranscription | 224 ns | 296 B |
| BuildText | 163 ns | 176 B |
| BuildToolCall | 360 ns | 384 B |
| BuildInterruption | 119 ns | 96 B |

After WS3 (source-generated UTF-8 `byte[]`, no anonymous type, no reflection; fixed envelopes cached
as `static readonly` arrays) — _to be filled in when WS3 lands_. Expected: fewer bytes, no reflection,
`BuildInterruption`/`BuildEnd` → **0 B** (returns a cached array).

---

## WS1 — Frame loop (`FrameLoopBenchmarks.Pump1000Frames`)

Validated by the xunit allocation gate `AllocationGateTests.DataLoop_SteadyState_DoesNotAllocatePerFrame`:
steady-state drain allocates **< 160 B/frame** (the irreducible `System.Threading.Channels`
async-wait floor is ~100 B/frame; the removed per-frame linked CTS added ~150 B/frame on top, so a
regression would push past the budget). Full BenchmarkDotNet capture _to be filled in at Phase 5_.

---

## WS2 — Transport receive path

Covered by `WebSocketAudioSourceFragmentationTests` for correctness (fast path, fragmented binary,
fragmented text, interleaved). Allocation profile: 1 exact-size copy per binary message (the frame
payload, which must own its array), pooled accumulator only for multi-receive messages — down from
2 copies + 2 allocations (`MemoryStream` grow + `ToArray`).
