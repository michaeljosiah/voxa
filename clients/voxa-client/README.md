# @voxa/client

The official browser/JavaScript client for [Voxa](https://github.com/michaeljosiah/voxa) voice
endpoints. It does the three things every consumer otherwise rebuilds by hand:

- **Mic capture** via AudioWorklet at the sample rate the *server* announces — nothing hardcoded.
- **Gap-free playback** of the bot's PCM at the announced output rate.
- **Barge-in that actually stops the bot**: the server already purges its queued audio on
  interruption, but only the browser can stop audio it has handed to the Web Audio graph. This
  client flushes local playback on both triggers (`speaking{who:"user",started:true}` and the
  `interruption` envelope).

Plus the piece the old test page skipped: **frontend tool round-trips** (`toolCall` event →
`sendToolResult`), and a **versioned protocol** — the types in `src/protocol.ts` are generated
from the server's C# envelope records via `voxa-wire.schema.json`, golden-checked on both sides,
and the client checks `session.v` at runtime.

## Usage

```ts
import { VoxaClient } from "@voxa/client";

const client = new VoxaClient({ url: "wss://example.com/voice" });

client.on("transcription", (t) => { if (t.isFinal) showUser(t.text); });
client.on("text", (t) => appendBot(t.text));           // streamed assistant tokens
client.on("speaking", (s) => setStatus(s));
client.on("toolCall", async (c) => {
  const result = await runFrontendTool(c.name, JSON.parse(c.argumentsJson));
  client.sendToolResult(c.callId, JSON.stringify(result));
});
client.on("micLevel", (rms) => meter.render(rms));
client.on("error", (e) => console.error(e.message));

await client.connect();  // opens the socket, reads the session envelope, starts the mic
// ... later
await client.disconnect();
```

The server side is the five-line Voxa endpoint: `app.MapVoxaVoice("/voice").UseDefaults()`.

## Options

| Option | Default | What it does |
|---|---|---|
| `url` | — | `ws(s)://host/voice` |
| `hello` | not sent | Sent as the FIRST message, before any audio — only for servers that opted into `UseWebSocketHello<T>` |
| `onVersionMismatch` | `"warn"` | `"warn" \| "throw" \| "ignore"` when `session.v` differs from `VOXA_WIRE_VERSION` |
| `audio` | `WebAudioBackend` | Inject a custom/fake `AudioBackend` (tests run headless this way) |
| `micConstraints` | EC+NS+AGC on, mono | Mic `MediaTrackConstraints` for the default backend |
| `socketFactory` | platform `WebSocket` | Injectable for tests |

## Development

```bash
npm install
npm test          # protocol golden check + tsc + headless unit suite (no browser needed)
npm run build     # generate:check + tsc → dist/
npm run generate  # regenerate src/protocol.ts from voxa-wire.schema.json
```

`voxa-wire.schema.json` is itself generated from the C# records by
`Voxa.Transports.WebSocket.Tests.WireSchemaGoldenTests` (regen with `VOXA_REGEN_GOLDEN=1`).
Change flow on an envelope change: edit the C# records → regen schema (.NET golden) →
`npm run generate` (TS golden) → both CI lanes prove the chain is consistent.
