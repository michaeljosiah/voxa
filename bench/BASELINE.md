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
as `static readonly` arrays):

| Method | Mean | Allocated | vs before |
|---|---|---|---|
| BuildTranscription | 145 ns | 136 B | −54% |
| BuildText | 106 ns | 88 B | −50% |
| BuildToolCall | 282 ns | 184 B | −52% |
| BuildInterruption | ~0 ns | **0 B** | cached array |

Note: the "before" numbers measured only the `string` build; the old sink then did a *separate*
`Encoding.UTF8.GetBytes(json)` per send (another ~100–200 B). The WS3 path returns the final UTF-8
bytes directly, so the real per-send reduction is larger than the table shows. Wire format verified
byte-for-byte unchanged by `WireProtocolCompatibilityTests`.

---

## WS1 — Frame loop (`FrameLoopBenchmarks.Pump1000Frames`)

After WS1 (`FrameLoopBenchmarks.Pump1000Frames`, ShortRun): **25 B/frame** allocated (with a
high-capacity channel so the reader rarely parks). The removed per-frame linked CTS cost ~150 B/frame
on top of this, so pre-WS1 was ~175+ B/frame. Also guarded by the xunit allocation gate
`AllocationGateTests.DataLoop_SteadyState_DoesNotAllocatePerFrame` (budget < 160 B/frame in the
bounded-64 scenario, where the `System.Threading.Channels` async-wait floor is ~100 B/frame).

---

## WS2 — Transport receive path

Covered by `WebSocketAudioSourceFragmentationTests` for correctness (fast path, fragmented binary,
fragmented text, interleaved). Allocation profile: 1 exact-size copy per binary message (the frame
payload, which must own its array), pooled accumulator only for multi-receive messages — down from
2 copies + 2 allocations (`MemoryStream` grow + `ToArray`).
