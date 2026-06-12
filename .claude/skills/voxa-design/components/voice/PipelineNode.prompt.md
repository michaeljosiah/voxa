One processor card in a VOXA frame pipeline. Stage sets the accent: `vad` grey · `stt` cyan · `agent` violet · `tts` amber · `out` green.

```jsx
const { PipelineNode } = window.VOXA;
<PipelineNode stage="llm" name="AgentTurn" meta="gpt-4o · p50 412 ms" active />
```

- Compose with `PipelineFlow` for the full chain.
- `meta` is mono — latency, model name, sample rate.
