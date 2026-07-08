using System.Text.Json;
using Voxa.Frames;

namespace Voxa.Transports.WebSocket.Protocol;

/// <summary>
/// JSON envelope codec for the Voxa WebSocket transport. Binary WebSocket frames carry raw PCM
/// audio; text WebSocket frames carry these typed JSON envelopes.
///
/// <para>
/// Outbound envelopes are serialized straight to UTF-8 bytes via source generation
/// (<see cref="WireJsonContext"/>) — no reflection, no anonymous-type allocation, no intermediate
/// string. The wire format is byte-for-byte identical to the previous reflection-based codec.
/// </para>
///
/// <para>
/// Client → Server: <c>{"type":"hello", ...}</c>, <c>{"type":"end"}</c>,
/// <c>{"type":"toolResult","callId":"...","resultJson":"..."}</c>,
/// <c>{"type":"text","text":"..."}</c>.
/// </para>
/// <para>
/// Server → Client: <c>{"type":"transcription", ...}</c>, <c>{"type":"text","text":"..."}</c>,
/// <c>{"type":"toolCall","callId":"...","name":"...","argumentsJson":"..."}</c>,
/// <c>{"type":"speaking","who":"bot|user","started":true}</c>, <c>{"type":"interruption"}</c>,
/// <c>{"type":"status","message":"..."}</c>, <c>{"type":"error","message":"..."}</c>,
/// <c>{"type":"end"}</c>.
/// </para>
/// </summary>
public static class WireProtocol
{
    /// <summary>
    /// Parse one inbound JSON envelope from the client and translate it into a <see cref="Frame"/>,
    /// or return null if the envelope is unrecognized or malformed (callers should drop unknowns).
    /// </summary>
    public static Frame? TryParseClientMessage(string json)
    {
        var byteCount = System.Text.Encoding.UTF8.GetByteCount(json);
        var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            var written = System.Text.Encoding.UTF8.GetBytes(json, rented);
            return TryParseClientMessage(rented.AsSpan(0, written));
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(rented); }
    }

    /// <summary>
    /// Parse one inbound UTF-8 JSON envelope from the client into a <see cref="Frame"/>, or return
    /// null if the envelope is unrecognized or malformed (callers should drop unknowns). Parses the
    /// UTF-8 bytes directly — no intermediate string.
    /// </summary>
    public static Frame? TryParseClientMessage(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8Json);
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type"u8, out var t)) return null;

            // VDX-005 WS1: each match deserializes its inbound record (the same source the wire
            // schema and the client's generated types come from) and maps to the same Frame as the
            // previous hand-rolled parse — same inputs, same frames, same dropped-unknown behavior.
            if (t.ValueEquals("end"u8)) return new EndFrame();
            if (t.ValueEquals("text"u8))
            {
                if (!root.TryGetProperty("text"u8, out _)) return null; // missing text drops, as always
                var env = root.Deserialize(WireJsonContext.Default.TextClientEnvelope);
                return new TextFrame(env.Text ?? string.Empty);
            }
            if (t.ValueEquals("toolResult"u8))
            {
                var env = root.Deserialize(WireJsonContext.Default.ToolResultClientEnvelope);
                return new ToolCallResultFrame(env.CallId ?? string.Empty, env.ResultJson ?? "{}", env.IsError);
            }
            return null; // includes "hello" (handled out-of-band) and unknowns
        }
        catch (JsonException) { return null; }
    }

    // ---------- Outbound (server -> client): source-generated, UTF-8, one allocation ----------

    /// <summary>Serialize a session-info envelope to UTF-8 JSON bytes.</summary>
    public static byte[] BuildSession(SessionInfoFrame f)
        => JsonSerializer.SerializeToUtf8Bytes(
            new SessionEnvelope("session", f.ProtocolVersion, f.InputSampleRate, f.OutputSampleRate),
            WireJsonContext.Default.SessionEnvelope);

    /// <summary>Serialize a transcription envelope to UTF-8 JSON bytes.</summary>
    public static byte[] BuildTranscription(TranscriptionFrame f)
        => JsonSerializer.SerializeToUtf8Bytes(
            new TranscriptionEnvelope("transcription", f.Text, f.IsFinal, f.Language, f.SpeakerId),
            WireJsonContext.Default.TranscriptionEnvelope);

    /// <summary>Serialize a text envelope to UTF-8 JSON bytes.</summary>
    public static byte[] BuildText(string text)
        => JsonSerializer.SerializeToUtf8Bytes(new TextEnvelope("text", text), WireJsonContext.Default.TextEnvelope);

    /// <summary>Serialize a tool-call envelope to UTF-8 JSON bytes.</summary>
    public static byte[] BuildToolCall(ToolCallRequestFrame f)
        => JsonSerializer.SerializeToUtf8Bytes(
            new ToolCallEnvelope("toolCall", f.CallId, f.Name, f.ArgumentsJson),
            WireJsonContext.Default.ToolCallEnvelope);

    /// <summary>Serialize a speaking-state envelope to UTF-8 JSON bytes.</summary>
    public static byte[] BuildSpeaking(string who, bool started)
        => JsonSerializer.SerializeToUtf8Bytes(new SpeakingEnvelope("speaking", who, started), WireJsonContext.Default.SpeakingEnvelope);

    // Fixed envelopes never vary — serialize once into static arrays so sends are zero-cost.
    private static readonly byte[] InterruptionBytes =
        JsonSerializer.SerializeToUtf8Bytes(new TypeOnlyEnvelope("interruption"), WireJsonContext.Default.TypeOnlyEnvelope);
    private static readonly byte[] EndBytes =
        JsonSerializer.SerializeToUtf8Bytes(new TypeOnlyEnvelope("end"), WireJsonContext.Default.TypeOnlyEnvelope);

    /// <summary>The fixed interruption envelope as UTF-8 JSON bytes (cached; do not mutate).</summary>
    public static byte[] BuildInterruption() => InterruptionBytes;

    /// <summary>The fixed end envelope as UTF-8 JSON bytes (cached; do not mutate).</summary>
    public static byte[] BuildEnd() => EndBytes;

    /// <summary>Serialize a status envelope to UTF-8 JSON bytes.</summary>
    public static byte[] BuildStatus(string message)
        => JsonSerializer.SerializeToUtf8Bytes(new MessageEnvelope("status", message), WireJsonContext.Default.MessageEnvelope);

    /// <summary>Serialize an error envelope to UTF-8 JSON bytes.</summary>
    public static byte[] BuildError(string message)
        => JsonSerializer.SerializeToUtf8Bytes(new MessageEnvelope("error", message), WireJsonContext.Default.MessageEnvelope);
}
