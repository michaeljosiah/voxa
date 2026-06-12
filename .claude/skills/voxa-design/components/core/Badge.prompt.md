Small uppercase mono pill for statuses, versions, stage tags.

```jsx
const { Badge } = window.VOXA;
<Badge tone="pulse">v0.4.0</Badge>
<Badge tone="ok">healthy</Badge>
```

- Tones map to semantics: `pulse` brand, `halo` agent/LLM, `ok`/`warn`/`danger`/`info` signals, `neutral` taxonomy.
- Copy inside is short — 1–2 words, no punctuation.
