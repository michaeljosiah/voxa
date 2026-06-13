# Voice library & cloning (VVL-001)

Voxa Studio's **Voices** section is a managed library of the voices your pipeline can use: the
voices a provider actually offers (fetched live, so the list never drifts from your account), the
voices you have **cloned**, and the local Piper/Kokoro catalogs — all in one place, each selectable
straight into a pipeline.

It is built on two small, capability-based framework seams that any TTS provider may implement, so
the same discovery and cloning work from a server too, not just Studio.

## What you can do

- **Discover** every voice a keyed provider offers — ElevenLabs and Mistral list live; Piper and
  Kokoro show their pinned catalogs.
- **Clone** a voice from a few seconds of audio to ElevenLabs or Mistral, name it, and use it
  immediately — without leaving Studio or hand-editing a config file.
- **Pick** a discovered or cloned voice in **Config** (and export the exact `appsettings.json`
  block) — the dropdown writes the provider-correct voice key for you.

> **Local, keyless cloning** (an ONNX tone-color converter, no account) is specified but **not yet
> shipped** — the wizard shows it as *coming soon*. See [§ Local cloning](#local-cloning-coming-soon).

## The Voices section

Open Studio and pick **Voices** in the nav rail.

- **Provider chips** (top) — one per TTS provider: how many voices it offers, or **key required**
  when no API key resolves. Add the key in the environment / user-secrets (or the Config tab's
  agent field for OpenAI) and press **Refresh**.
- **The library grid** — every voice, tagged by how it reconciles against the provider's live truth:
  - **Live** — a voice you saved that the provider still lists.
  - **Discovered** — a live provider voice you haven't saved yet (**add** to annotate it locally).
  - **Stale** — a saved voice the provider no longer lists (deleted, or the key changed). Shown
    dimmed; **forget** it or re-clone. A stale voice is never silently used in a pipeline.
  - **LocalCatalog** — a compiled-in Piper/Kokoro voice.
- **audition** deep-links the voice into the TTS Playground (real synthesis + playback).

### Clone a voice

1. In the **Clone a voice** panel, give it a **Name**.
2. **Add file…** one or more clean reference clips (WAV/MP3/FLAC/M4A) — or record one (recording is
   paused while a Talk/Builder/Metrics run holds the audio device).
3. Pick a **Clone to** target (a cloud provider whose key resolves).
4. Tick the **consent** box: *"I have the right to clone this voice — it is my own, or one I am
   licensed to use."* The **Create voice** button stays disabled until a name, a sample, a target,
   and consent are all present.
5. **Create voice.** On success the clone appears in the library, ready to select in Config.

> **Consent is required, by design.** Cloning a non-consenting person's voice is a non-goal of this
> feature. The attestation is recorded with the saved voice; cloud providers also enforce their own
> consent terms (e.g. ElevenLabs Instant Voice Clone is plan-gated — a rejection shows the
> provider's message and saves nothing).

## Using a cloned / cloud voice in a pipeline

In **Config**, choosing a cloud TTS provider (ElevenLabs / Mistral) shows a **Voice** dropdown
populated live from the library — your clones included. Selecting one writes the provider-correct
key into the exported block:

| Provider   | Key written            |
| ---------- | ---------------------- |
| ElevenLabs | `Voxa:ElevenLabs:VoiceId` |
| Mistral    | `Voxa:Mistral:Voice`   |

The export never contains an API key — only the voice id. Paste the block into your server, supply
the key via the environment (`Voxa__ElevenLabs__ApiKey`, `Voxa__Mistral__ApiKey`) or user-secrets,
and the pipeline speaks in the cloned voice.

```json
{
  "Voxa": {
    "Tts": "ElevenLabs",
    "ElevenLabs": { "VoiceId": "your-cloned-voice-id" }
  }
}
```

## Voxtral speech-to-text

Mistral's **Voxtral** transcription ships as a new `Mistral` STT provider — select it wherever you'd
pick WhisperCpp/OpenAI/Azure STT. It is **utterance-buffered**: the REST API is request/response, so
the engine posts the whole utterance at speech-end and returns one final transcript (no incremental
partials in v1). It pairs naturally with the **Quality** profile.

```json
{ "Voxa": { "Stt": "Mistral", "Mistral": { "SttModel": "voxtral-mini-latest" } } }
```

Needs `Voxa:Mistral:ApiKey`.

## Where things live

- **Library** — `~/voxa-voices/`, one JSON profile per voice plus the reference clips you cloned
  from. **No API key is ever written here** — a profile is a pointer (provider + remote voice id),
  your own samples, a consent timestamp, and metadata. The folder is portable and diffable.
- **Keys** — the standard `Voxa:*` config surface (`appsettings.json` / environment / user-secrets).
  Sent only to their own provider over TLS, never persisted by the library or any export.

## Using the seams from a server (no Studio)

The capabilities are framework features. A host can list or clone voices through the registry:

```csharp
var voxaRoot = configuration.GetSection("Voxa");
if (registry.TryGetVoiceCatalog("ElevenLabs", services, voxaRoot, out var catalog))
    foreach (var v in await catalog.ListVoicesAsync(ct))
        Console.WriteLine($"{v.DisplayName} ({v.Kind})");

if (registry.TryGetVoiceCloner("ElevenLabs", services, voxaRoot, out var cloner))
    await cloner.CreateVoiceAsync(new VoiceCloneRequest("My Voice", samples), ct);
```

A TTS provider opts in by setting `ResolveCatalog` / `ResolveCloner` on its `VoxaTtsDescriptor`;
providers whose voices are a compiled-in list (Piper, Kokoro) leave them null.

## Local cloning (coming soon)

The spec ([VVL-001](specifications/voxa-voice-library-spec.html)) defines a keyless local clone
engine — an MIT-licensed OpenVoice v2 tone-color converter run as ONNX over the Kokoro base TTS,
reusing the SHA-256-pinned model cache. It is **deferred** pending a validated ONNX export and an
audio-quality spike (the spec's WS3-A0 gate); the clone wizard shows it as *coming soon*. Cloud
cloning delivers the feature today.
