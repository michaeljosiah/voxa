# Voxa.Samples.TwilioServer

A minimal phone-call voice agent: a Twilio number rings, Twilio streams the call audio to this server over a
WebSocket, and Voxa runs its standard `VAD → STT → agent → TTS` pipeline on it — talking back down the call,
with barge-in. See [VTL-001](../../docs/specifications/vtl-001-telephony-transport-spec.html).

The whole server is five lines (`Program.cs`):

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddVoxa(builder.Configuration);
var app = builder.Build();
app.UseWebSockets();
app.MapVoxaTwilioVoice("/twilio");   // webhook (TwiML) + /twilio/media (WebSocket)
app.Run();
```

## Run it

You need a [Twilio](https://www.twilio.com/) account with a voice-capable phone number, and a tunnel that
gives your local server a public HTTPS URL (Twilio must reach it from the internet).

1. **Start the server.**
   - Cloud providers (needs an OpenAI key — set `Voxa:OpenAI:ApiKey` via user-secrets or env):
     ```bash
     dotnet run --project samples/Voxa.Samples.TwilioServer
     ```
   - **Fully local, no API keys** (downloads ~250 MB of models on first run; signature validation off for tunnels):
     ```bash
     dotnet run --project samples/Voxa.Samples.TwilioServer --launch-profile Local
     ```
   The server listens on `http://localhost:5180`.

2. **Expose it with a tunnel.** For example with [ngrok](https://ngrok.com/):
   ```bash
   ngrok http 5180
   ```
   Copy the public URL it prints, e.g. `https://abc123.ngrok.io`.

3. **Point your Twilio number at the webhook.** In the Twilio console → your number → *Voice → A call comes in*,
   set a **Webhook** to `https://abc123.ngrok.io/twilio` (HTTP POST). Save.

4. **Call the number.** Twilio fetches TwiML from `/twilio`, which tells it to open
   `wss://abc123.ngrok.io/twilio/media`; the call audio flows through the pipeline and the agent talks back.

## Production notes

- **Signature validation.** Keep `Voxa:Telephony:Twilio:ValidateSignature` **on** and set
  `Voxa:Telephony:Twilio:AuthToken` (from the Twilio console). It's off in the `Local` profile only because a
  tunnel rewrites the host, so the URL Twilio signs won't match what the server sees. Behind a real reverse
  proxy, apply forwarded headers so `Request.Host`/`Scheme` reflect the public URL, then keep validation on.
- **`PublicWssBaseUrl`.** If the public host differs from what the server sees (proxy/tunnel), set
  `Voxa:Telephony:Twilio:PublicWssBaseUrl` (e.g. `wss://abc123.ngrok.io`) so the TwiML points Twilio at the
  right `wss://` host.
- **Live-wire caveat.** The Twilio wire protocol is parser/spec-tested in this repo but should be validated on a
  real call before you rely on it — see the [`Voxa.Transports.Twilio`](../../src/Voxa.Transports.Twilio/README.md) README.
