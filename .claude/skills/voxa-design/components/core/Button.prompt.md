Action button for VOXA surfaces — use `primary` (cyan fill) for the single main action, `secondary` for everything else, `ghost` for toolbars, `danger` for destructive actions.

```jsx
const { Button } = window.VOXA;
<Button variant="primary" size="md">Deploy pipeline</Button>
<Button variant="secondary" icon={<i data-lucide="copy" style={{width:14}} />}>Copy key</Button>
```

- Exactly one `primary` per view; primary glows on hover.
- `size`: sm (30px), md (38px), lg (46px hero CTAs).
- Press state nudges 1px down — no scale.
