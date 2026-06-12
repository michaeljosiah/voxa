Default container surface: `--surface-card` fill, hairline border, 14px radius, top edge-light.

```jsx
const { Card } = window.VOXA;
<Card interactive>…</Card>
<Card glow>…the one highlighted item…</Card>
```

- `interactive` for clickable list items (hover raises border + surface).
- `glow` reserved for a single emphasized card per view (live session, recommended plan).
