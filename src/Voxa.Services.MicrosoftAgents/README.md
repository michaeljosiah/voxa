# Voxa.Services.MicrosoftAgents

Adapter that wraps any [Microsoft Agent Framework](https://learn.microsoft.com/en-us/agent-framework) `AIAgent` as a [Voxa](https://github.com/michaeljosiah/voxa) `FrameProcessor`.

## Install

```bash
dotnet add package Voxa.Services.MicrosoftAgents --prerelease
```

## Quickstart

```csharp
using Microsoft.Agents.AI;

var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    Name = "my-agent",
});

var pipeline = Pipeline.Build()
    .Source(new WebSocketAudioSource(ws))
    .Then(new AzureSpeechSttProcessor(speechOpts))
    .Then(new MicrosoftAgentsProcessor(agent))   // ← here
    .Then(new AzureSpeechTtsProcessor(speechOpts))
    .Sink(new WebSocketAudioSink(ws));
```

## What's included

- `MicrosoftAgentsProcessor` — each final-form `TranscriptionFrame` (or `TextFrame`) becomes a user message; the agent's streamed response is fanned out as `LlmTextChunkFrame` + `ToolCallRequestFrame`. Optional `AgentSession` for conversation history.

Use this for the granular STT → Agent → TTS path. The Voice Live composite path (`AzureVoiceLiveProcessor`) embeds the agent server-side and doesn't need this package.

Targets `Microsoft.Agents.AI` 1.5.0.

## License

MIT.
