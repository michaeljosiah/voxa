The frame-pipeline diagram — VOXA's central metaphor. Dashed links carry a traveling frame dot when `running`.

```jsx
const { PipelineFlow } = window.VOXA;
<PipelineFlow running activeIndex={2} nodes={[
  { stage: 'audio', name: 'WebRtcTransport', meta: '48 kHz' },
  { stage: 'stt', name: 'AzureSpeechStt', meta: 'p50 118 ms' },
  { stage: 'llm', name: 'AgentTurn', meta: 'gpt-4o' },
  { stage: 'tts', name: 'AzureNeuralTts', meta: 'en-US-Ava' },
]} />
```

- Wrap in a horizontally-scrollable container for long chains.
