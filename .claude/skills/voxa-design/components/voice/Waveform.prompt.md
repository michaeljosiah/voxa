Voice-activity bars — VOXA's core motif. Use it anywhere audio is "speaking": session rows, hero art, call controls.

```jsx
const { Waveform } = window.VOXA;
<Waveform live gradient bars={16} height={32} />
<Waveform muted bars={8} height={16} />
```

- `live` animates; static renders a fixed pattern (safe for print/screenshot).
- `gradient` is deprecated (the brand sanctions no gradient) and now renders solid cyan; use plain cyan everywhere.
