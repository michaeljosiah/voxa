# Voxa Studio — UI kit

High-fidelity recreation of **Voxa Studio**, the Windows-first Avalonia 11 desktop app specified in **VST-002 — Voxa Studio 2.0 Design Brief**. Built entirely from the design-system tokens + primitives; cosmetic, not production code.

`index.html` boots the **launch splash**, then the desktop shell. Use the nav rail to move between views; the titlebar's ✨ replays the splash.

## Surfaces

| File | What |
|---|---|
| `index.html` | App entry — splash → shell, view routing, session clock (interactive `@dsCard`). |
| `splash.html` | The borderless launch splash on its own (`@dsCard`, 480×320). |
| `Splash.jsx` | Animated launch: mark draws on, bars spring, wordmark settles, microcopy ticks init stages. Static-capture safe; honors reduced-motion. |
| `StudioShell.jsx` | Custom titlebar (mark · VOXA STUDIO · live dot · session timer · window controls) + icon nav rail + view host. Exports `ViewBar`. |
| `PipelineBuilder.jsx` + `canvasNodes.jsx` | **Builder** node canvas (§8): registry palette, typed ports (stage-colored), bezier edges, per-node inspector, run-from-canvas with live edge flow + node glow + bottom turn ticker. Single-in/single-out (the §8.3 honesty constraint). |
| `MetricsWorkbench.jsx` | **Metrics** (§9): voice-to-voice TTFB percentile card, per-turn stage-stacked bars with a plain-language takeaway, run list + compare/export. |
| `SttPlayground.jsx` | **STT lab** (§6): source strip, model selector, transcript cards with final latency + waveform, live WER harness with ins/sub diff coloring, side-by-side. |
| `TtsPlayground.jsx` | **TTS lab** (§7): voice catalog (TTFB/RTF), synthesis waveform scrubber, take history, A/B/X blind test, stress phrases, batch bench. |
| `TalkView.jsx` | **Talk** (v1 role): streaming transcript, VoiceOrb + live waveform, running pipeline flow, session metrics. |
| `SupportingViews.jsx` | Playgrounds wrapper (STT/TTS segmented), Models cache manager, Config composer ("open as graph"). |
| `icons.js` | React-safe Lucide renderer (injects SVG into React-owned `<i>` instead of `createIcons`, which replaces nodes and breaks reconciliation). |
| `app.jsx` | Mount + routing. |

## Notes
- Stage colors (`vad/stt/agent/tts/out`) are the §3.3 palette and mean the same thing on the canvas ports, the metrics bars, and the Talk waterfall — learn one, read all.
- All numbers are mono (Cascadia Code); the brief's "if it's data, it's mono" rule.
- Icons: Lucide via CDN, stroke 1.75 (flagged substitution until the brand cuts its own set).
