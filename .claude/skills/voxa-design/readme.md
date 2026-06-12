# VOXA Design System

**VOXA** is a frame-based, real-time voice AI pipeline framework for **.NET** — inspired by Pipecat, built around **Microsoft Agent Framework** and **Azure services** (Speech, OpenAI, ACS telephony). Developers compose voice agents as pipelines of frame processors in C#: transport → STT → agent → TTS, with interruptions and barge-in modeled as frames in the stream.

**Sources:** the visual system follows **VST-002 — Voxa Studio 2.0 Design Brief** (`uploads/voxa-studio-design-brief.html`, v1.0, 2026-06-12), which defines the mark, motion, color, and type. No production codebase or Figma was provided; the brief is the source of truth. **Voxa Studio** is the brief's primary surface — a Windows-first Avalonia 11 desktop app (`apps/Voxa.Studio`) for running the real pipeline locally and watching it think: Talk, STT/TTS Playgrounds, a node-canvas Pipeline Builder, and a Run & Metrics workbench.

**Reference surfaces in this repo** (web/desktop recreations sharing the foundations — nothing has shipped):
- **Voxa Studio** (`ui_kits/studio/`) — the brief's desktop app: launch splash, shell, Builder node canvas, Run & Metrics workbench, STT/TTS playgrounds, Talk.
- **VOXA Console** — operator dashboard: live sessions, pipelines, transcripts, latency.
- **voxa.dev** — developer marketing site with C# quickstart.

---

## Content fundamentals

**Tone:** confident, technical, economical. Written by engineers who respect the reader's time. Futurism lives in the visuals — the copy stays plain.

- **Sentence case everywhere** — headings, buttons, nav, badges. Never Title Case, never ALL CAPS in prose (uppercase belongs only to mono micro-labels).
- **Address the reader as "you"; the product is "VOXA"** (all caps in prose, `Voxa` in package/code identifiers). No "we believe…" mission language.
- **Verb-led CTAs:** "Get started", "Deploy pipeline", "End session". One to three words.
- **No emoji. Ever.** Signal comes from color and the status-dot system instead.
- **Numbers are evidence:** copy leans on concrete figures — "<500 ms round trip", "p50 118 ms", "48 kHz". Always with units, always mono-set in UI.
- **Frame vocabulary:** frames, processors, pipelines, transports, turns, barge-in, interruptions. Say "frame", not "event" or "message".
- Headline pattern: short declarative + technical qualifier. *"Voice agents, frame by frame."*
- Errors/status are terse and lowercase in mono contexts: `frame: interruption · agent yielded in 96 ms`.

## Visual foundations

**Vibe (P2):** dark, calm, one accent. Near-black blue-grey surfaces, hairline borders, generous whitespace, a single cyan accent doing all the talking. *If a screen looks exciting while idle, it's wrong.* Color beyond cyan is **semantic only** (stage colors, good/warn/bad).

- **Dark-only.** Page `--ink-950 #0B0F14`, panel `--ink-900 #11161D`, card `--ink-850 #161C26`, raised `--ink-800 #1C2330`, hairline base `--ink-700 #222B36`. There is no light theme.
- **Color:** one accent — **Pulse** cyan `--pulse-400 #4FC3F7` (CTAs, live states, edge flow, the STT stage). `--pulse-300 #81D4FA` is the lighter cyan for micro-labels/section heads. **Halo** violet `#CE93D8` is semantic-only (agent/LLM). Semantics: `--ok #66BB6A`, `--warn #FFB74D`, `--danger #EF5350`, `--info #4FC3F7`.
- **Stage palette (the five fixed stages, §3.3):** the same five colors mean the same five stages everywhere — waterfall bars, node ports, edge flows, metric series. `vad #76849B` (grey) · `stt #4FC3F7` (cyan) · `agent #CE93D8` (violet) · `tts #FFB74D` (amber) · `out #66BB6A` (green). Learn "violet = agent" once and read every surface for free.
- **Text on cyan is always ink, never white.**
- **No gradient.** The brand sanctions none — "a single cyan accent doing all the talking; the mark never fills with gradients." `--grad-signal*` are retained only as solid-cyan fallbacks. No decorative graphics that aren't data (P1).
- **Type:** Inter (UI) · Cascadia Code (every number, id, timestamp — *if it's data, it's mono*). Scale 11/12 dense · 13 body · 15 section · 20 view title. The letter-spaced wordmark caps (+0.42em) are reserved for brand moments (titlebar, splash, empty states).
- **Backgrounds:** flat ink, no imagery, no textures. Depth comes from one soft radial accent glow behind heroes, max one per page.
- **Borders:** 1px white-alpha hairlines — `--line-1` (10%) resting, `--line-2` (16%) controls, `--line-3` (28%) hover. Cards: `--surface-card` + `--line-1` + `--shadow-1` + `--edge-light` (1px inner top highlight), radius 14px.
- **Radii:** 6 chips · 10 controls · 14 cards · 20 modals · pill for status. App-icon tile ~23%.
- **Shadows:** black ambient (`--shadow-1/2/3`) for elevation; **glow is meaning** — `--glow-pulse` (cyan) marks live/primary things only.
- **Hover:** surface lightens one step + border to `--line-3`; primary buttons add glow. **Press:** 1px translateY, no scale. **Focus:** 2px cyan ring offset by 2px ink (`--ring-focus`).
- **Motion (P3 — motion has a job).** Four tokens: `fast 120ms`, `standard 240ms` (both `--ease-out` cubic-bezier(.2,.8,.2,1)); `draw 1100ms` `--ease-draw` cubic-bezier(.6,0,.2,1) (logo draw-on, first chart paint); `pulse 2600ms` ease-in-out infinite — **the only idle loops** (live dot, logo glow), both data-backed. Every animation respects reduced-motion. No bounces, no parallax.
- **Transparency/blur:** only sticky chrome (topbars/navs) uses `--glass-bg` + 14px blur.
- **Layout:** content max 1120px; console sidebar fixed 232px on `--bg-panel`; 24px gutters; 4px spacing grid.
- **Signature motifs:** bottom-aligned waveform bars (the logo's V-envelope), the dashed frame-travel pipeline link, the glowing VoiceOrb, mono micro-labels with `·` separators.

## Iconography

- **System:** [Lucide](https://lucide.dev) loaded from CDN (`https://unpkg.com/lucide@latest` + `lucide.createIcons({attrs:{'stroke-width':1.75}})`). Stroke icons only, 1.75 weight, `currentColor`; 16px inside controls, 20px standalone. No icon binaries live in this repo — this is a flagged substitution until the brand cuts its own set.
- **Core glyphs:** mic, audio-lines, phone, bot, workflow, activity, terminal, key-round, settings-2, cloud.
- **No emoji, no filled icons, no unicode-as-icon** (the only sanctioned glyphs in text are `·` separators and `▲▼` deltas in metrics).

## Index

| Path | What |
|---|---|
| `styles.css` | Global entry — imports everything below |
| `tokens/` | colors · typography · spacing · effects · fonts · base |
| `components/components.css` | All `vx-*` component classes |
| `components/core/` | Button, IconButton, Badge, Card, Tabs, CodeBlock |
| `components/forms/` | Input, Select, Switch |
| `components/voice/` | Waveform, VoiceOrb, StatusPill, PipelineNode, PipelineFlow, TranscriptLine, MetricStat |
| `assets/` | voxa-logo.svg · voxa-mark.svg · voxa-icon.svg · voxa-wordmark-mono.svg |
| `guidelines/` | Foundation specimen cards (Colors, Type, Spacing, Brand) |
| `ui_kits/studio/` | **Voxa Studio** desktop app — splash, Builder canvas, Metrics, STT/TTS labs, Talk |
| `ui_kits/console/` | VOXA Console — interactive dashboard |
| `ui_kits/website/` | voxa.dev — marketing landing |
| `SKILL.md` | Agent-skill entry point |

Components mount from the compiled bundle: `const { Button, Waveform } = window.VOXADesignSystem_4f47fa` (load `_ds_bundle.js` after React).

**Fonts:** the brief specifies **Inter** (UI, shipped in-app via `Avalonia.Fonts.Inter`) and **Cascadia Code** (data). `tokens/fonts.css` loads Inter from Google Fonts and **Cascadia Code** as a real self-hosted `@font-face` from the Fontsource CDN (variable 200–700, OFL-1.1 — the same family Microsoft ships). On the Windows-first target the locally installed Cascadia Code is used first. If you'd rather vendor the woff2 in-repo instead of the CDN, send the file and I'll swap the `src`.
