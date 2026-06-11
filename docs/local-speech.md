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
is local. Swap `"Echo"` for `"OpenAI"` (plus a key) when you want a real LLM — or register any
`IChatClient` in DI (e.g. an Ollama client) for a fully local brain.

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
