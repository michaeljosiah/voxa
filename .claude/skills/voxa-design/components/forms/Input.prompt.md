Labeled text field. Use `mono` for anything machine-shaped (API keys, endpoints, IDs).

```jsx
const { Input } = window.VOXA;
<Input label="Endpoint" mono placeholder="wss://voxa.example.dev/session" />
<Input label="Name" error="A pipeline with this name already exists" />
```

- Focus = cyan border + soft outer glow; error = red border + red hint line.
- onChange receives the string value directly.
