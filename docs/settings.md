# Voxa Studio — Settings

The **Settings** dialog is where you manage *which providers Studio can use* and *the credentials
they need*. Open it from the **gear** at the foot of the nav rail. It is a modal dialog, not a nav
section — it sits over whatever view you're on.

> **Spec:** [VST-003](specifications/vst-003-settings-dialog-spec.html).

## The Providers tab

A master–detail layout: the list of your providers on the left, the selected provider's
configuration on the right.

Each row carries a status dot:

| Dot | Meaning |
|-----|---------|
| 🟢 Green | **Configured** — every required key is filled. |
| 🟠 Amber | **Key missing** — added, but a required field is still blank. |
| ⚪ Grey | **Local** — runs on-device, no credentials needed. |

### Adding a provider

Click **+ Add provider** to open a card-grid of everything Studio supports that you haven't added
yet. Pick a card and it joins your list, selected and ready for its key. Supported cloud providers:

| Provider | Roles it fills | Fields |
|----------|----------------|--------|
| **OpenAI** | STT · TTS · Agent | API Key |
| **Azure Speech** | STT · TTS | Subscription Key, Region |
| **ElevenLabs** | TTS | API Key |
| **Mistral** | STT · TTS | API Key |

**One key, several roles.** Some identities wear several hats. Enter your OpenAI key once and it
powers Whisper speech-to-text, OpenAI text-to-speech, *and* the OpenAI chat agent — all three show
up in Config's dropdowns at once. Mistral covers both STT (Voxtral) and TTS the same way.

### Local providers

**Whisper, Piper, Kokoro,** and **Echo** are always in the list, below the cloud providers, with a
grey dot and no fields. They run entirely on your machine, need no credentials, and can't be
removed — they're shown so the list is a complete inventory of what Studio can do.

### Saving

**Save** writes your changes; **Cancel** discards them. Nothing is persisted until you Save, so a
Cancel (or closing the window) leaves everything exactly as it was.

## Appearance

The **Appearance** category lets you pick a theme. Changes apply **immediately** (no Save needed) and
persist to `~/voxa-studio-prefs.json`, so your choice is restored next launch.

| Theme | Look |
|-------|------|
| **Warm** | Warm-neutral surfaces, coral accent (default). |
| **Cool** | The original Voxa cool-ink surfaces, cyan accent. |
| **Slate** | Neutral slate-grey surfaces, soft blue accent. |

Switching repaints the whole app live — surfaces, text, accent, the brand mark, and the Talk bubbles
all move together. The five **pipeline stage colours** (VAD/STT/agent/TTS/output, used in the latency
waterfall, the VAD trace, Builder ports and the Metrics charts) deliberately stay fixed across themes,
because those colours encode meaning, not decoration.

## Where your keys are stored

On Windows, keys are encrypted with **DPAPI** (the Data Protection API — the same mechanism Windows
uses for saved browser passwords) and written to `~/voxa-secrets.dpapi`. The ciphertext is tied to
your Windows user account: another user on the same machine, or the same file copied elsewhere,
can't decrypt it. The activation list (which providers you've switched on — names only, no secrets)
is plain JSON at `~/voxa-activations.json`.

Keys are decrypted into memory only when Studio starts and when you Save. They:

- are **live from the next launch** — no environment variables, no re-typing each session;
- are **never written into any export** (`appsettings.json` blocks, Builder bundles, the clipboard);
- **never leave the machine** except as a request to their own provider over TLS.

A returning user's providers are ready from the first Talk session, with no Apply step.

## How Settings relates to Config

- **Settings** decides *which providers exist and what keys they hold.*
- **Config** decides *which pipeline a session runs* (and per-provider model choices).

Config's STT / TTS / Agent dropdowns show only providers that are **local** or **activated in
Settings** — so a fresh Studio offers just the local tier until you add a cloud provider. Activate
ElevenLabs in Settings and it appears in Config's TTS dropdown immediately, no restart.

When you Save Settings, the new credentials are pushed into the running app as a dedicated
configuration layer. A later Config **Apply** swaps the pipeline selection on top of that layer — it
**won't** wipe your stored keys.

## For server deployments

Studio's Settings store is a *Studio* convenience; it isn't read by a Voxa server. To run the same
providers on a server, set the standard `Voxa:*` keys via environment variables or user-secrets, e.g.
`Voxa__OpenAI__ApiKey`, `Voxa__ElevenLabs__ApiKey`, `Voxa__AzureSpeech__SubscriptionKey` +
`Voxa__AzureSpeech__Region`. Config's exported `appsettings.json` block gives you the non-secret
shape; add the keys through your platform's secret manager.

## Platform note

DPAPI is Windows-only. On other platforms Studio falls back to an in-memory store (keys live for the
session only). A cross-platform encrypted backend (macOS Keychain / Linux libsecret) is future work.
