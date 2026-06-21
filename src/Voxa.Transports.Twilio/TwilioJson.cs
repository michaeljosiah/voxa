using System.Text.Json;
using System.Text.Json.Serialization;

namespace Voxa.Transports.Twilio;

// Outbound (server -> Twilio) envelope DTOs. CamelCase reproduces Twilio's documented wire shapes:
//   media: {"event":"media","streamSid":"...","media":{"payload":"<base64 μ-law>"}}
//   clear: {"event":"clear","streamSid":"..."}
//   mark:  {"event":"mark","streamSid":"...","mark":{"name":"..."}}
internal readonly record struct MediaEnvelope(string Event, string StreamSid, MediaPayload Media);
internal readonly record struct MediaPayload(string Payload);
internal readonly record struct ClearEnvelope(string Event, string StreamSid);
internal readonly record struct MarkEnvelope(string Event, string StreamSid, MarkBody Mark);
internal readonly record struct MarkBody(string Name);

/// <summary>
/// Source-generated serialization for the Twilio outbound envelopes — straight to UTF-8 bytes, no reflection
/// and no intermediate string (same approach as the native transport's <c>WireProtocol</c>).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    GenerationMode = JsonSourceGenerationMode.Serialization)]
[JsonSerializable(typeof(MediaEnvelope))]
[JsonSerializable(typeof(ClearEnvelope))]
[JsonSerializable(typeof(MarkEnvelope))]
internal sealed partial class TwilioJsonContext : JsonSerializerContext;

/// <summary>Builders for the three outbound Twilio Media Streams messages.</summary>
internal static class TwilioJson
{
    public static byte[] Media(string streamSid, ReadOnlySpan<byte> muLaw)
        => JsonSerializer.SerializeToUtf8Bytes(
            new MediaEnvelope("media", streamSid, new MediaPayload(Convert.ToBase64String(muLaw))),
            TwilioJsonContext.Default.MediaEnvelope);

    public static byte[] Clear(string streamSid)
        => JsonSerializer.SerializeToUtf8Bytes(
            new ClearEnvelope("clear", streamSid),
            TwilioJsonContext.Default.ClearEnvelope);

    public static byte[] Mark(string streamSid, string name)
        => JsonSerializer.SerializeToUtf8Bytes(
            new MarkEnvelope("mark", streamSid, new MarkBody(name)),
            TwilioJsonContext.Default.MarkEnvelope);
}
