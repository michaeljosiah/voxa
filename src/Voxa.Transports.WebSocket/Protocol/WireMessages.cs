using System.Text.Json.Serialization;

namespace Voxa.Transports.WebSocket.Protocol;

// Outbound (server -> client) envelope DTOs. Field order matches the historical anonymous-type
// output so the wire bytes are byte-for-byte unchanged. 'Type' is always first.
internal readonly record struct SessionEnvelope(string Type, int V, int InputSampleRate, int OutputSampleRate);
internal readonly record struct TranscriptionEnvelope(string Type, string Text, bool IsFinal, string? Language, string? SpeakerId);
internal readonly record struct TextEnvelope(string Type, string Text);
internal readonly record struct ToolCallEnvelope(string Type, string CallId, string Name, string ArgumentsJson);
internal readonly record struct SpeakingEnvelope(string Type, string Who, bool Started);
internal readonly record struct TypeOnlyEnvelope(string Type);                   // interruption, end
internal readonly record struct MessageEnvelope(string Type, string Message);    // status, error

/// <summary>
/// Source-generated serialization context for the outbound wire envelopes. Eliminates reflection
/// and the anonymous-type allocation; serializes straight to UTF-8 bytes. CamelCase + omit-nulls
/// reproduce the historical wire format exactly.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Serialization)]
[JsonSerializable(typeof(SessionEnvelope))]
[JsonSerializable(typeof(TranscriptionEnvelope))]
[JsonSerializable(typeof(TextEnvelope))]
[JsonSerializable(typeof(ToolCallEnvelope))]
[JsonSerializable(typeof(SpeakingEnvelope))]
[JsonSerializable(typeof(TypeOnlyEnvelope))]
[JsonSerializable(typeof(MessageEnvelope))]
internal sealed partial class WireJsonContext : JsonSerializerContext;
