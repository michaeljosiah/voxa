# Voxa.Transports.Twilio

**Phone calls in and out of a [Voxa](https://github.com/michaeljosiah/voxa) voice agent**, over
[Twilio Media Streams](https://www.twilio.com/docs/voice/media-streams) (VTL-001). A caller dials your
Twilio number, Twilio bridges the call audio to your server over a **WebSocket**, and Voxa runs its normal
`VAD → STT → agent → TTS` pipeline on it — speaking back down the call, with barge-in. **No WebRTC, no SIP.**

It builds on [`Voxa.Transports.Telephony`](https://www.nuget.org/packages/Voxa.Transports.Telephony) (the
shared source/sink + μ-law codec) and composes the **same** pipeline as the native `MapVoxaVoice(...).UseDefaults()`
route — only the edge source/sink differ.

## Usage

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddVoxa(builder.Configuration);   // same providers/profile as any Voxa host
var app = builder.Build();

app.UseWebSockets();
app.MapVoxaTwilioVoice("/twilio");                 // webhook (TwiML) + /twilio/media (WebSocket)
app.Run();
```

Point your Twilio number's **Voice webhook** at `https://<your-host>/twilio`. Twilio fetches TwiML, then
opens `wss://<your-host>/twilio/media` for the call audio.

### Configuration (`appsettings.json`)

```jsonc
{
  "Voxa": {
    "Stt": "WhisperCpp",
    "Tts": "Kokoro",
    "Telephony": {
      "Twilio": {
        "ValidateSignature": true,   // verify X-Twilio-Signature on the webhook (recommended)
        "AuthToken": "",             // for signature validation (prefer env/secrets over appsettings)
        "PublicWssBaseUrl": ""       // override the wss host in TwiML behind a proxy/tunnel, e.g. wss://abc.ngrok.io
      }
    }
  }
}
```

| Key | Default | Meaning |
| --- | --- | --- |
| `ValidateSignature` | `true` | Verify Twilio's request signature on the webhook before returning TwiML. Disable only for local tunnels where the signed URL won't match the request URL the server sees. |
| `AuthToken` | — | Twilio auth token for signature validation. **Required** when `ValidateSignature` is on (the webhook fails closed if missing). |
| `PublicWssBaseUrl` | request host | Override the `wss://` host embedded in TwiML when the public URL differs from `Request.Host` (reverse proxy, ngrok). |

## Security

The webhook validates Twilio's `X-Twilio-Signature` (HMAC-SHA1 over the request URL + sorted form params) by
default — without it, anyone who learns the URL can drive your pipeline and run up cost. Keep the route on
**TLS** (`wss`), and keep the auth token out of source control. Behind a tunnel/proxy, either apply forwarded
headers so `Request.Host`/`Scheme` reflect the public URL, or disable validation for that environment.

## ⚠️ Live-wire caveat

Like the streaming STT providers, the Twilio wire protocol here is **parser/spec-tested only** until run
against the real service. The envelope shapes are from Twilio's public Media Streams docs and are stable, but
exact field presence, base64 framing, chunk cadence, and `clear`/`mark` timing **must be validated on a real
call** before relying on this in production. The codec parses defensively (unknown events are ignored), but a
real call (number → webhook via a public URL/tunnel → confirm two-way audio, barge-in, clean hang-up) is the
load-bearing check.

Pre-alpha; the public API still moves.
