Underline tabs for switching sibling views inside a panel.

```jsx
const { Tabs } = window.VOXA;
<Tabs tabs={['Transcript', 'Frames', 'Metrics']} onChange={setView} />
```

- Active tab gets a 2px `--pulse-400` underline; labels are sentence case.
- Works controlled (`active`) or uncontrolled (`defaultTab`).
