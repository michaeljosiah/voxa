using System.Text.Json;
using System.Text.Json.Serialization;
using Voxa.Frames;

namespace Voxa.Transports.WebSocket.Protocol;

/// <summary>
/// JSON envelope codec for the Voxa WebSocket transport. Binary WebSocket frames carry raw PCM
/// audio; text WebSocket frames carry these typed JSON envelopes.
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
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

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

            if (t.ValueEquals("end"u8)) return new EndFrame();
            if (t.ValueEquals("text"u8))
                return root.TryGetProperty("text"u8, out var txt)
                    ? new TextFrame(txt.GetString() ?? string.Empty)
                    : null;
            if (t.ValueEquals("toolResult"u8)) return ParseToolResult(root);
            return null; // includes "hello" (handled out-of-band) and unknowns
        }
        catch (JsonException) { return null; }
    }

    private static Frame ParseToolResult(JsonElement root)
    {
        var callId = root.TryGetProperty("callId", out var c) ? (c.GetString() ?? string.Empty) : string.Empty;
        var resultJson = root.TryGetProperty("resultJson", out var r) ? (r.GetString() ?? "{}") : "{}";
        var isError = root.TryGetProperty("isError", out var e) && e.ValueKind == JsonValueKind.True;
        return new ToolCallResultFrame(callId, resultJson, isError);
    }

    public static string BuildTranscription(TranscriptionFrame f)
        => JsonSerializer.Serialize(new
        {
            type = "transcription",
            text = f.Text,
            isFinal = f.IsFinal,
            language = f.Language,
            speakerId = f.SpeakerId,
        }, JsonOpts);

    public static string BuildText(string text)
        => JsonSerializer.Serialize(new { type = "text", text }, JsonOpts);

    public static string BuildToolCall(ToolCallRequestFrame f)
        => JsonSerializer.Serialize(new
        {
            type = "toolCall",
            callId = f.CallId,
            name = f.Name,
            argumentsJson = f.ArgumentsJson,
        }, JsonOpts);

    public static string BuildSpeaking(string who, bool started)
        => JsonSerializer.Serialize(new { type = "speaking", who, started }, JsonOpts);

    public static string BuildInterruption()
        => JsonSerializer.Serialize(new { type = "interruption" }, JsonOpts);

    public static string BuildStatus(string message)
        => JsonSerializer.Serialize(new { type = "status", message }, JsonOpts);

    public static string BuildError(string message)
        => JsonSerializer.Serialize(new { type = "error", message }, JsonOpts);

    public static string BuildEnd()
        => JsonSerializer.Serialize(new { type = "end" }, JsonOpts);
}
