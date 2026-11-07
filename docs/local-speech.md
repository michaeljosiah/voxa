# Local / offline speech (VLS-001)

Run a complete Voxa voice bot — hearing, thinking, speaking — **without an API key, a cloud
account, or (after first run) a network connection**. Spec:
[`docs/specifications/voxa-local-speech-spec.html`](specifications/voxa-local-speech-spec.html).

## Quickstart

```json
{
  "Voxa": {
    "Stt": "WhisperCpp",
    "Tts": "Piper",
    "Agent": { "Provider": "Echo" }
  }
}
```

That's the whole switch. The five-line `Program.cs` from the main README is unchanged. First run
downloads the models (SHA-256-verified, with progress logged at startup); afterwards everything
is local. Swap `"Echo"` for `"OpenAI"` (plus a key) when you want a cloud LLM, or `"Ollama"` for a
fully-local brain — point `Voxa:Agent:Provider` at a running [Ollama](https://ollama.com) daemon
(keyless; set `Voxa:Agent:Model` to a pulled model such as `llama3.2`, and `Voxa:Agent:BaseUrl` if it
is not on `http://localhost:11434/v1`). You can still register any `IChatClient` in DI to override.

### Try it in a browser

The `MinimalServer` sample ships this config as `appsettings.Local.json` plus a tiny test page:

```bash
dotnet run --project samples/Voxa.Samples.MinimalServer --launch-profile Local
```

Open <http://localhost:5170>, allow the mic, and talk. The page derives both sample rates from the
server's `session` envelope (it hardcodes nothing), so every voice plays at the right pitch:
16 kHz `en_US-amy-low`, 22.05 kHz `en_US-lessac-medium`, 24 kHz Kokoro. It also flushes playback on
barge-in. First run downloads the models, so give it a moment before the first reply.

## The three local providers

| Provider | Role | Default model | Quality / speed |
|---|---|---|---|
| `WhisperCpp` | STT | `base.en` (~148 MB) | Good accuracy; transcribes per utterance (see latency below) |
| `Piper` | TTS | `en_US-lessac-medium` (~63 MB) | The **speed tier**: RTF ≈ 0.05 on CPU, decent voices |
| `Kokoro` | TTS | `af_heart` fp16 (~163 MB) | The **quality tier**: rivals commercial cloud voices, heavier on CPU |

### Choosing between Piper and Kokoro

- **Piper** for CI, weak hardware, fastest first-audio, and non-English coverage today
  (`en_GB-alan-medium`, `de_DE-thorsten-medium`, `fr_FR-siwis-medium`, `es_ES-davefx-medium`).
- **Kokoro** when the voice is the product: noticeably more natural, fixed 24 kHz output (the
  same rate as the cloud TTS default, so swapping ElevenLabs→Kokoro doesn't change the wire
  session at all). Use `"Precision": "int8"` (~92 MB) on weak machines.

## Arke desktop sidecar

`Voxa.ArkeSidecar` is a Windows executable for embedding local speech in a desktop product such as
Arke Studio. It is not a public speech server and it is separate from the in-process provider flow
described above: the desktop host owns its lifetime, chooses a free loopback port, supplies every
speech asset, waits for health, and degrades its speech UI if the child exits or an engine is
unavailable.

```text
Arke desktop host
  |-- starts/stops the architecture-matched Voxa.ArkeSidecar.exe
  |-- supplies explicit asset paths and a loopback port
  `-- HTTP on loopback only
        |-- Kokoro ONNX synthesis in the sidecar process
        |-- espeak-ng child process for phonemization
        `-- WhisperCpp transcription in the sidecar process
```

### Network and privacy boundary

- The listener defaults to `127.0.0.1:5901`. `--host` accepts only a literal loopback IP
  (`127.0.0.1` or `::1`); hostnames, `0.0.0.0`, LAN addresses, and public addresses are rejected.
- Inherited ASP.NET Core and Kestrel configuration is cleared, so environment settings cannot add a
  second public listener. Requests whose remote address is not loopback receive `403`.
- The API has no TLS or authentication and is intentionally reachable only from the local machine.
  It is a same-machine desktop component, not a network deployment surface.
- The sidecar has no cloud provider configuration and accepts no cloud credentials. Its model caches
  are opened in offline mode, so requests do not trigger model downloads or cloud speech calls.
- Application logging providers are removed. Request text, audio, and transcripts are not logged.
  Windows WebM decoding uses a temporary `.webm` file and attempts deletion after decoding or
  failure; hosts that require no transient content on disk should send PCM16 or WAV instead.

### Launch contract and assets

Arguments are discrete `--name value` pairs, so paths containing spaces remain one argument:

```powershell
& ".\Voxa.ArkeSidecar.exe" `
  --host 127.0.0.1 `
  --port 5901 `
  --kokoro-model "C:\Arke\Speech\kokoro\model.onnx" `
  --kokoro-config "C:\Arke\Speech\kokoro\config.json" `
  --kokoro-voices "C:\Arke\Speech\kokoro\voices" `
  --whisper-model "C:\Arke\Speech\whisper\ggml-base.en.bin" `
  --espeak "C:\Arke\Speech\espeak\espeak-ng.exe" `
  --espeak-data "C:\Arke\Speech\espeak\espeak-ng-data"
```

| Argument | Default | Required for capability | Meaning |
|---|---|---|---|
| `--host` | `127.0.0.1` | Service | Literal IPv4 or IPv6 loopback address. |
| `--port` | `PORT`, then `5901` | Service | Integer from 1 through 65535; an explicit argument takes precedence over `PORT`. |
| `--kokoro-model` | none | TTS | Existing Kokoro ONNX model file. |
| `--kokoro-config` | none | TTS | Existing Kokoro config file. |
| `--kokoro-voices` | none | TTS | Directory containing `af_bella.bin`, `af_nicole.bin`, `am_michael.bin`, `am_adam.bin`, `bf_emma.bin`, and `bm_george.bin`. |
| `--whisper-model` | none | STT | Existing Whisper GGML model file. |
| `--espeak` | none | TTS | Existing espeak-ng executable. |
| `--espeak-data` | none | TTS | Existing `espeak-ng-data` directory, or its parent directory. |

Models, voice files, the Kokoro config, and espeak-ng are **external deployment inputs**. They are not
embedded in `Voxa.ArkeSidecar`, copied by `dotnet publish`, or downloaded by the running service.
This differs deliberately from the general-purpose `VoxaModelCache`: the embedding host is
responsible for acquisition, licensing, integrity checks, placement, and passing the exact paths.

Kokoro runs in-process inside the sidecar, but phonemization invokes the supplied espeak-ng CLI as a
separate child process and sends text over standard input. No Voxa assembly links espeak-ng; this is
the GPL-3.0 boundary. The embedding application's distribution of espeak-ng and its data remains a
separate licensing responsibility.

Invalid argument shape, an unknown argument, a non-loopback host, or an invalid port prevents startup;
argument errors write a short message to standard error and set exit code `2`. Missing speech assets
do **not** prevent startup, which lets the desktop host inspect `/health` and present a degraded state.

### HTTP protocol version 1

All JSON uses camel-case property names. `GET /health` returns HTTP `200` while the service is
running. A fully provisioned x64 process has this shape:

```json
{
  "ok": true,
  "version": "0.8.0",
  "protocolVersion": 1,
  "architecture": "x64",
  "engines": ["kokoro", "whisper"],
  "engineStatus": {
    "kokoro": { "ready": true },
    "whisper": { "ready": true }
  }
}
```

`architecture` is `x64`, `arm64`, or `unsupported`. `engines` names the protocol's engines; it is not
an availability list. When assets are missing or an engine has failed, health adds a path-free reason:

```json
{
  "ok": true,
  "version": "0.8.0",
  "protocolVersion": 1,
  "architecture": "arm64",
  "engines": ["kokoro", "whisper"],
  "engineStatus": {
    "kokoro": { "ready": false, "reason": "Kokoro is unavailable." },
    "whisper": { "ready": true }
  },
  "unavailableReason": "Kokoro is unavailable."
}
```

For protocol version 1, `ok: true` means the HTTP sidecar is alive; it does not guarantee both speech
engines are ready. Hosts inspect `engineStatus` to gate each feature independently. The aggregate
`unavailableReason` remains intentionally generic and does not expose configured paths or exceptions.

| Endpoint | Request | Success response | Relevant failures |
|---|---|---|---|
| `GET /health` | none | Health JSON above. | A stopped/unreachable process is detected by the host. |
| `GET /voices` | none | Static array of the six configured Kokoro voice descriptors. | The catalog remains available even when Kokoro assets are missing. |
| `POST /tts` | JSON `{ "voice": "af_bella", "text": "Hello", "speed": 1.0 }` | Complete WAV body, `audio/wav`, at 24 kHz. | `400` for malformed input, unknown voice, empty/oversize text, or speed outside `0.92`, `1.0`, and `1.15`; `503` when Kokoro is unavailable. |
| `POST /stt` | Audio request body, maximum 64 MiB. | JSON `{ "text": "hello" }`. | `413` over 64 MiB; `415` for empty, malformed, or unsupported audio; `503` when Whisper is unavailable. |
| `POST /stt/words` | Same audio formats and 64 MiB bound as `/stt`. | Transcript, duration, engine identity and word intervals described below. | Same input/unavailable failures as `/stt`; `422` when native timestamps cannot form usable word intervals. |

`POST /stt` accepts 16 kHz mono PCM16 WAV (`audio/wav` or `audio/x-wav`), raw PCM16
(`audio/pcm`, `audio/L16`, `application/octet-stream`, or no content type), and on Windows WebM
(`audio/webm`) decoded through Media Foundation. `audio/L16` follows network byte order and is
converted before inference. STT is batch transcription in English, not a streaming endpoint.
`POST /tts` returns a complete WAV rather than streaming. TTS text is capped at 10,000 characters;
synthesis is limited to two concurrent requests and transcription to one, bounding CPU and memory.

### Optional word timing

New sidecars add `wordTiming: { "ready": true }` to health when the Whisper assets are available;
an unavailable capability includes a generic, path-free `reason`. Older sidecars omit this field.
Hosts must treat an absent or false capability as unavailable, independently of plain transcription.
Requests to `/stt/words` share the single STT inference gate and cancel when the caller disconnects.

```json
{
  "text": "Hello harbour.",
  "seconds": 1.8,
  "engine": {
    "id": "whisper.cpp/dtw-word-boundaries-v1",
    "version": "1.9.1.0",
    "model": "sha256:<digest-of-loaded-model>"
  },
  "words": [
    { "text": "Hello", "startSec": 0.2, "endSec": 0.51, "probability": 0.97 },
    { "text": "harbour.", "startSec": 0.72, "endSec": 1.45, "probability": 0.89 }
  ]
}
```

The numbers above illustrate the response shape. Intervals are seconds in the submitted audio;
duration comes from decoded PCM. Cross-attention DTW supplies token boundaries at 20 ms resolution.
Each token starts at its DTW boundary and ends at the next token boundary across recognition segments,
with the final token ending at the decoded window boundary. Subwords and punctuation join using the
outer bounds and lowest token probability. The first sustained near-silence in the decoded PCM tightens the end boundary
(10 ms energy windows, at least 120 ms below -70 dBFS). Silent intervals and unresolved intervals
longer than 2.5 seconds are rejected. This is an explicit alignment interval convention; it does not
claim phoneme-level speech offsets. The sidecar never uses Whisper's heuristic `Start`/`End` token fields, which can split
duration proportionally by text length. Invalid, overlapping, zero-duration or indivisible multi-word
bounds return `422` rather than guessed timings. Native ASR timestamps are estimates, not a guarantee of alignment
accuracy: the host must validate transcript correspondence and confidence before highlighting authored
words, and bind saved timing to the exact audio and text. Silence can return an empty transcript and
word array. A rejected recording does not mark the engine unavailable; a runtime failure does.
The pinned native library can omit later DTW callbacks beyond its first 30-second window.
Inference therefore uses windows of at most 28 seconds, split only inside measured silence,
with absolute audio offsets preserved. A long recording without a safe silent split returns `422`.
DTW currently supports the catalog-pinned `base.en` model supplied by Arke. Its SHA-256 selects the
matching alignment heads, independent of filename; other models leave plain STT usable but refuse
word timing. No assets are downloaded or upgraded by this endpoint.

Validate the sidecar with `dotnet test tests/Voxa.ArkeSidecar.Tests --filter "Category!=LocalModels"`.
The additional `Category=LocalModels` test uses the checked-in JFK recording, a version repeated
past 30 seconds with deliberate pauses, and cached `base.en`;
on first run the test's ordinary SHA-256-verified cache resolver provisions the model. The CI cache
epoch includes this additional model before its network-blocked pass. Set `VOXA_WORD_TIMING_TEST_MODEL`
to use an explicitly provisioned `base.en` file without test-time downloads. The noisy historic
fixture contains low-confidence words: endpoint success verifies transport and acoustic bounds,
not eligibility to highlight that transcript in a host with a stricter confidence gate.

### Failure and degradation behavior

- Kokoro and Whisper availability is independent. Missing TTS assets do not disable STT, and a
  missing Whisper model does not disable TTS.
- Asset absence is re-evaluated by health and before each request. A runtime initialization or
  inference failure marks that engine unavailable for the rest of the sidecar process; restart after
  fixing or replacing its assets.
- An unavailable engine returns an RFC 7807 problem response with `503`. There is no automatic cloud
  fallback, credential lookup, or model download.
- Hosts should treat a failed health call, process exit, protocol mismatch, or `503` as local speech
  degradation, disable only the affected feature where possible, and keep the desktop application
  running. `unavailableReason` is suitable for a general status message, not detailed diagnostics.

### Self-contained Windows publishing

Build one self-contained directory for each supported Windows architecture from the repository root:

```powershell
dotnet publish src/Voxa.ArkeSidecar/Voxa.ArkeSidecar.csproj -c Release -r win-x64 --self-contained true -o publish/Voxa.ArkeSidecar-win-x64
dotnet publish src/Voxa.ArkeSidecar/Voxa.ArkeSidecar.csproj -c Release -r win-arm64 --self-contained true -o publish/Voxa.ArkeSidecar-win-arm64
```

Each output directory is an architecture-specific deployment unit containing the executable, .NET
runtime, managed dependencies, ONNX Runtime, and WhisperCpp native runtime. Keep the entire directory
together and launch the RID that matches the desktop process/machine. The commands above create local
publish outputs only: this repository does not currently publish Arke sidecar release artifacts or
run a smoke test against those packaged self-contained directories. Existing sidecar tests exercise
argument parsing, loopback rules, endpoint contracts, and audio adaptation against the built project.

## Voxtral — cloud-grade STT, fully offline (VLS-009)

whisper.cpp is keyless and runs anywhere, but it has no interims and its accurate models are slow on CPU
(see latency below). If you have a **GPU (≥ 16 GB)**, `Voxa.Speech.Voxtral` runs Mistral's open-weights
**Voxtral-Mini-4B-Realtime** (Apache-2.0) entirely on your machine via a local **vLLM** server — streaming,
cloud-grade transcription with live interims, no API key, no audio leaving the box.

```jsonc
"Voxa": {
  "Stt": "Voxtral",
  // connect to a vLLM server you run …
  "Voxtral": { "ServerUrl": "ws://127.0.0.1:8000" }
  // … or have Voxa launch & own it:
  // "Voxtral": { "LaunchCommand": "vllm",
  //   "LaunchArgs": ["serve", "mistralai/Voxtral-Mini-4B-Realtime-2602", "--tokenizer-mode", "mistral"] }
}
```

Run the server once on a GPU box: `vllm serve mistralai/Voxtral-Mini-4B-Realtime-2602 --tokenizer-mode mistral`.
No GPU handy? `sidecar/voxtral_realtime_mock.py` replays a canned transcript over the same wire so you can
smoke-test the pipeline offline. In **Voxa Studio**, Voxtral becomes the default STT automatically on a capable
GPU once a server is configured — otherwise whisper.cpp. vLLM is the only supported runtime today (no ONNX/CPU
path yet); see the [package README](../src/Voxa.Speech.Voxtral/README.md) for all the knobs.

## Latency expectations — read this before filing a bug

Cloud streaming STT shows interim transcripts while the user talks. **WhisperCpp does not**: it
transcribes once per utterance after the VAD detects speech-end, so the bot's reply starts
roughly `transcription time` later than a cloud pipeline would. Rough CPU figures (4 threads):

| Model | RTF | 5 s utterance → transcript in |
|---|---|---|
| `tiny.en` | ~0.1–0.2 | ~0.5–1 s |
| `base.en` | ~0.2–0.5 | ~1–2.5 s |
| `small.en` | ~0.5–1.5 | ~2.5–7 s |

For interactive development: `tiny.en`/`base.en` + `"Profile": "LowLatency"`.

### Bigger models & GPU (VLS-002)

The catalog also carries the larger Whisper families for higher accuracy — `medium` / `medium.en`,
`large-v3`, and `large-v3-turbo` (each with a `-q5_0` quantization), SHA-256-pinned like the rest
([spec](specifications/vls-002-gpu-stt-catalog-spec.html)). They are **far slower than real time on
CPU** — `large-v3` is ~3.1 GB and only practical on a GPU — so the engine logs a warning when a
large/medium model runs on CPU.

Pick the inference backend with `Voxa:WhisperCpp:Device` (default `cpu`):

```json
{
  "Voxa": {
    "Stt": "WhisperCpp",
    "WhisperCpp": { "Model": "large-v3-turbo", "Device": "cuda" }
  }
}
```

| `Device` | Behaviour |
|---|---|
| `cpu` *(default)* | CPU only — deterministic, no extra packages. What CI uses. |
| `auto` | Best available accelerator, falling back to CPU. |
| `cuda` / `vulkan` / `coreml` | Require that runtime; **fail at startup** if it can't load (no silent CPU fallback). |

**Voxa never bundles GPU natives** — the default package stays CPU-only and small. To use a GPU, add
the matching runtime to *your* app and set `Device`:

```xml
<PackageReference Include="Whisper.net.Runtime.Cuda" Version="1.9.1" />
```

`large-v3-turbo` is the sweet spot: ~8× faster than `large-v3` with near-large accuracy. `coreml` is
best-effort — it needs an extra Core ML encoder bundle that VLS-002 does not yet provision.

## The model cache

| Setting | Default | Notes |
|---|---|---|
| `VOXA_MODEL_CACHE` (env) | — | Highest-precedence cache root override (CI/containers) |
| `Voxa:Models:CachePath` | `%LOCALAPPDATA%\voxa\models` / `~/.cache/voxa/models` | Cache root |
| `Voxa:Models:Offline` | `false` | `true` = never download; a miss is a startup error with provisioning instructions |
| `Voxa:Models:EagerWarmup` | `true` | The startup guard resolves models and pre-loads weights so the first caller never pays |

Downloads are pinned: each artifact's URL **and SHA-256** are compiled into the provider package,
so an upstream re-upload or tampering fails loudly. Corporate proxies are honoured via the
`IHttpClientFactory` integration (`AddHttpClient("Voxa")`).

## Air-gap runbook

On a machine **with** internet:

1. Set `VOXA_MODEL_CACHE` to a staging directory and run the app (or the LocalModels test suite)
   once — the cache populates through the verified resolver.
2. Copy the directory to the target machine (USB, artifact store, …).

On the air-gapped machine:

3. Point `VOXA_MODEL_CACHE` (or `Voxa:Models:CachePath`) at the copied directory.
4. Set `"Voxa:Models:Offline": true`.
5. Start the host. If anything is missing, the startup error names the artifact, the exact
   expected path, the pinned URL, and the SHA-256 — hand that text to whoever runs your transfer
   process.

## Zero-cost CI conversations

The repo's own `local-speech` CI lane is the reference implementation
([.github/workflows/ci.yml](../.github/workflows/ci.yml)):

1. `actions/cache` keyed on the catalog files restores the models (~200 MB: whisper `tiny.en`,
   piper `en_US-amy-low` + binary, Kokoro int8 + espeak-ng).
2. On a cache miss, one networked run of `--filter "Category=LocalModels"` populates it.
3. The suite then runs with outbound HTTP black-holed (`HTTPS_PROXY=http://127.0.0.1:9`) — proving
   the zero-network claim on every PR, with zero secrets configured.

The flagship test (`LocalConversationEndToEndTests`) streams real recorded speech through the real
Silero VAD → whisper → Echo agent → Piper and asserts on the audio that comes back.

## Troubleshooting

- **"No pinned piper/espeak-ng build exists for this platform"** — install the tool from your
  package manager and set `Voxa:Piper:ExecutablePath` / `Voxa:Kokoro:EspeakPath`, or just add it
  to `PATH`.
- **Apple Silicon (macOS arm64)** — there is deliberately no auto-download here: the upstream
  `*_macos_aarch64` release assets ship x86_64 binaries mislabeled as aarch64, which fail to start
  on Apple Silicon without Rosetta. Install native arm64 builds instead — `brew install piper`
  and/or `brew install espeak-ng` — and Voxa picks them up from `PATH` automatically (or set the
  explicit path options). Intel Macs (`osx-x64`) auto-download as normal.
- **Hash mismatch on download** — retry once (corrupted transfer); if it persists the upstream
  file changed and the catalog pin caught it — update the Voxa packages.
- **A stale `.lock` file after a hard crash** — delete it; the error message names the path.
- **piper process diagnostics** — piper's stderr tail is included in any synthesis failure;
  pooled processes are killed on host exit (no orphans).
- **GPL note** — espeak-ng (used by both Piper internally and Kokoro's phonemization) is GPL-3.0
  and always runs as a separate process. No Voxa assembly links GPL code; a dependency-graph gate
  test enforces this.


### Arke transcription name hints

`POST /stt` keeps its raw audio body and optional `X-Voxa-Prompt` header: base64 of UTF-8
vocabulary text, at most 200 decoded bytes, without null characters. Malformed hints return
400 before transcription; a missing or empty hint keeps the ordinary transcription behavior.
The small byte bound conservatively fits Whisper's initial context even for non-ASCII names.
Hints belong to one request's processor, never the shared model or a later dictation request.
`WhisperCppOptions.InitialPrompt` is also available to hosts constructing an engine directly.
This does not add hints to `/stt/words` or change transcription acceptance thresholds.
