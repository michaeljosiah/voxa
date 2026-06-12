---
name: voxa-design
description: Use this skill to generate well-branded interfaces and assets for VOXA (frame-based real-time voice AI framework for .NET), either for production or throwaway prototypes/mocks/etc. Contains essential design guidelines, colors, type, fonts, assets, and UI kit components for prototyping.
user-invocable: true
---

Read the README.md file within this skill, and explore the other available files.
If creating visual artifacts (slides, mocks, throwaway prototypes, etc), copy assets out and create static HTML files for the user to view. If working on production code, you can copy assets and read the rules here to become an expert in designing with this brand.
If the user invokes this skill without any other guidance, ask them what they want to build or design, ask some questions, and act as an expert designer who outputs HTML artifacts _or_ production code, depending on the need.

Key facts for quick use:
- Dark-only brand: page `#0B0F14`, panel `#11161D`, accent cyan `#4FC3F7` (text on cyan is ink, never white), agent violet `#CE93D8` semantic-only. **One accent only** — no gradients.
- Five fixed stage colors (everywhere): vad `#76849B` · stt `#4FC3F7` · agent `#CE93D8` · tts `#FFB74D` · out `#66BB6A`. Semantics: ok `#66BB6A`, warn `#FFB74D`, danger `#EF5350`.
- Type: Inter (UI) / Cascadia Code (every number, id, timestamp — if it's data, it's mono). Letter-spaced caps for the wordmark only.
- Motion: fast 120 · standard 240 · draw 1100 · pulse 2600 (only idle loop, data-backed). Always respect reduced-motion.
- Link `styles.css` for all tokens + `vx-*` component classes; React components live under `components/`.
- Icons: Lucide CDN, stroke 1.75. No emoji.
- Signature motifs: bottom-aligned waveform bars, dashed frame-travel pipeline links, glowing VoiceOrb, uppercase mono micro-labels.
