using System.Text.Json;
using System.Text.Json.Serialization;

namespace Voxa.Speech.Voxtral;

/// <summary>The kind of server event <see cref="VoxtralWire.TryParseServerMessage"/> recognised.</summary>
internal enum VoxtralServerEvent
{
    /// <summary>An incremental partial (<c>transcription.delta</c>) — display only.</summary>
    Delta,

    /// <summary>The finalized utterance transcript (<c>transcription.done</c>).</summary>
    Done,
}

/// <summary>One parsed server message: the <see cref="Kind"/> and its text payload (the delta, or the final text).</summary>
internal readonly record struct VoxtralServerMessage(VoxtralServerEvent Kind, string Text);

/// <summary>
/// The vLLM realtime WebSocket envelopes (OpenAI-Realtime-style JSON) for Voxtral transcription. Outbound builders
/// return UTF-8 bytes ready for a text WebSocket frame; <see cref="TryParseServerMessage"/> is pure and total —
/// an unknown <c>type</c> (e.g. <c>session.created</c>) or a malformed frame yields <c>false</c> and is ignored,
/// never throwing out of the receive loop.
/// </summary>
internal static class VoxtralWire
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Both commit forms are constant — build once. The non-final form OMITS the "final" field (per vLLM's
    // realtime protocol: a bare commit flushes/starts the stream; only {"final":true} ends it).
    private static readonly byte[] CommitBytes =
        JsonSerializer.SerializeToUtf8Bytes(new CommitMsg("input_audio_buffer.commit"), Json);
    private static readonly byte[] FinalCommitBytes =
        JsonSerializer.SerializeToUtf8Bytes(new FinalCommitMsg("input_audio_buffer.commit", true), Json);

    /// <summary>Handshake: tell the server which model this session uses.</summary>
    public static byte[] SessionUpdate(string model)
        => JsonSerializer.SerializeToUtf8Bytes(new SessionUpdateMsg("session.update", model), Json);

    /// <summary>Append a PCM16 chunk as base64 (<c>input_audio_buffer.append</c>).</summary>
    public static byte[] AppendAudio(ReadOnlySpan<byte> pcm)
        => JsonSerializer.SerializeToUtf8Bytes(new AppendMsg("input_audio_buffer.append", Convert.ToBase64String(pcm)), Json);

    /// <summary>
    /// Commit the buffered audio. <paramref name="final"/> <c>false</c> (the default) flushes the current
    /// utterance without ending the stream — also the "ready to start" signal sent right after the handshake;
    /// <c>true</c> signals end-of-all-audio. vLLM reserves <c>{"final":true}</c> for session end, so sending it
    /// per-utterance would stop transcription after the first one.
    /// </summary>
    public static ReadOnlyMemory<byte> Commit(bool final = false) => final ? FinalCommitBytes : CommitBytes;

    /// <summary>Parse one server text frame. Returns false (ignored) for any non-transcription or malformed message.</summary>
    public static bool TryParseServerMessage(string json, out VoxtralServerMessage message)
    {
        message = default;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var typeEl) ||
                typeEl.ValueKind != JsonValueKind.String)
                return false;

            switch (typeEl.GetString())
            {
                case "transcription.delta":
                    message = new VoxtralServerMessage(VoxtralServerEvent.Delta, StringProp(root, "delta"));
                    return true;
                case "transcription.done":
                    message = new VoxtralServerMessage(VoxtralServerEvent.Done, StringProp(root, "text"));
                    return true;
                default:
                    return false; // session.created / unknown — not our concern
            }
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string StringProp(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? string.Empty
            : string.Empty;

    private sealed record SessionUpdateMsg(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("model")] string Model);

    private sealed record AppendMsg(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("audio")] string Audio);

    private sealed record CommitMsg(
        [property: JsonPropertyName("type")] string Type);

    private sealed record FinalCommitMsg(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("final")] bool Final);
}
