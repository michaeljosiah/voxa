One row of a live transcript — role gutter, utterance, mono timestamp.

```jsx
const { TranscriptLine } = window.VOXA;
<TranscriptLine role="user" text="What's my order status?" time="00:12.480" />
<TranscriptLine role="agent" text="Your order shipped this morning —" partial />
```

- `partial` shows a blinking caret for streaming text.
- `system` role for frame events (interruption, tool call) in mono.
