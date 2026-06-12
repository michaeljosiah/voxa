Dark code panel for C# samples — filename bar, lang badge, light keyword/type/string highlighting.

```jsx
const { CodeBlock } = window.VOXA;
<CodeBlock file="Program.cs" badge="C#" code={`var pipeline = new VoxaPipeline()\n    .Use<AzureSpeechStt>()\n    .Use<AgentTurn>("support-agent")\n    .Use<AzureNeuralTts>();`} />
```

- Highlighting is cosmetic (keywords violet, types cyan, strings amber) — fine for mocks, not a real parser.
- Omit `file`/`badge` to render a bare code surface.
