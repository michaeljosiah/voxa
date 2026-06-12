Connection/session state indicator — mono uppercase text with a signal dot. `live` pulses cyan, `connecting` blinks amber.

```jsx
const { StatusPill } = window.VOXA;
<StatusPill status="live" />
<StatusPill status="ended" label="Ended 4m ago" />
```
