<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="../assets/voxa-logo-animated.svg">
    <img src="../assets/voxa-logo-animated-light.svg" alt="VOXA" width="260">
  </picture>
</p>

# Voxa documentation

Guides for using VOXA, and the design specifications behind each delivery.

## Guides

| Guide | What it covers |
|---|---|
| [Background agent delegation](background-agent.md) | The talker/thinker split (VDX-008): a fast interaction model keeps the conversation flowing while a heavyweight background agent runs tools, browsing, and reasoning off the voice-latency critical path. |
| [Voxa Studio](studio.md) | The desktop app: Talk, the STT/TTS playgrounds, model-cache manager, config composer, server-side diagnostics, troubleshooting. |
| [Local speech](local-speech.md) | The keyless local tier — WhisperCpp, Piper, Kokoro, Silero VAD; model cache, air-gapped provisioning, licensing. |
| [Performance tuning](performance-tuning.md) | The latency knobs and what they trade; the `LowLatency`/`Quality`/`Cheap` profiles. |

## Specifications

Design documents for each delivery, in `specifications/` (HTML — view raw or locally):

- `voxa-developer-experience-spec.html` — VDX-001: `AddVoxa`, the provider registry, profiles, the composer.
- `voxa-local-speech-spec.html` — VLS-001: the local speech tier.
- `voxa-performance-optimization-spec.html` — VPS-001: hot-loop allocations, wire contract, latency workflow.
- `voxa-studio-spec.html` — VST-001: Studio v1 and the diagnostics hub.
- `voxa-studio-design-brief.html` — VST-002: Studio 2.0 — brand, playgrounds, builder, metrics.

Repo-level docs: [README](../README.md) · [ROADMAP](../ROADMAP.md) · [CHANGELOG](../CHANGELOG.md) · [CONTRIBUTING](../CONTRIBUTING.md)
