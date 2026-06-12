Icon-only square button for toolbars, table rows and panel headers. Always pass `label`.

```jsx
const { IconButton } = window.VOXA;
<IconButton label="Mute" outline><i data-lucide="mic-off" style={{width:16}} /></IconButton>
```

- Default is borderless ghost; `outline` adds border + surface for standalone use.
- Icons: Lucide at 16px, stroke 1.75.
