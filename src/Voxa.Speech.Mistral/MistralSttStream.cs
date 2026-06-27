using System.Text.Json;

namespace Voxa.Speech.Mistral;

/// <summary>
/// Pure parser for Mistral's streaming transcription Server-Sent Events
/// (<c>POST /v1/audio/transcriptions</c> with <c>stream=true</c>). The response is an
/// <c>text/event-stream</c> of JSON payloads: incremental <c>transcription.text.delta</c> events carry partial
/// text, and a terminal <c>transcription.done</c> (a.k.a. <c>transcription.text.done</c>) carries the settled
/// transcript. The exact field names are under-documented, so parsing is deliberately <b>tolerant and total</b>:
/// a delta's text is read from <c>delta</c> or <c>text</c>, the done text from <c>text</c>, and any unknown or
/// malformed payload is classified <see cref="MistralSttEventKind.Other"/> (ignored) rather than throwing.
/// </summary>
internal static class MistralSttStream
{
    /// <summary>
    /// Strip the SSE <c>data:</c> field prefix from one line. Returns false for non-data lines
    /// (comments, <c>event:</c>/<c>id:</c> fields, blank event boundaries).
    /// </summary>
    internal static bool TryReadDataLine(string line, out string payload)
    {
        payload = string.Empty;
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return false;
        // SSE allows an optional single leading space after the colon.
        payload = line.AsSpan(5).TrimStart(' ').ToString();
        return payload.Length > 0;
    }

    /// <summary>True for the optional <c>[DONE]</c> sentinel some OpenAI-compatible servers send to close the stream.</summary>
    internal static bool IsDoneSentinel(string payload) =>
        payload.Equals("[DONE]", StringComparison.Ordinal);

    /// <summary>
    /// Parse one SSE data payload into a typed event. Returns <c>null</c> for malformed JSON or a non-object
    /// payload; returns <see cref="MistralSttEventKind.Other"/> for a well-formed but unrecognized event
    /// (session/language-detection frames) so the caller can ignore it without special-casing.
    /// </summary>
    internal static MistralSttEvent? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            string? language = root.TryGetProperty("language", out var l) ? l.GetString() : null;

            string? text = null;
            if (root.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
                text = txt.GetString();
            else if (root.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.String)
                text = d.GetString();

            if (type is not null && type.Contains("done", StringComparison.OrdinalIgnoreCase))
                return new MistralSttEvent(MistralSttEventKind.Done, text ?? string.Empty, language);
            if (type is not null && type.Contains("delta", StringComparison.OrdinalIgnoreCase))
                return new MistralSttEvent(MistralSttEventKind.Delta, text ?? string.Empty, language);

            return new MistralSttEvent(MistralSttEventKind.Other, text ?? string.Empty, language);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Classifies a parsed streaming-transcription event.</summary>
internal enum MistralSttEventKind
{
    /// <summary>Incremental partial text to append to the running transcript (emit as an interim).</summary>
    Delta,
    /// <summary>Terminal event carrying the settled transcript (emit as the final).</summary>
    Done,
    /// <summary>A well-formed but unrecognized event — ignore.</summary>
    Other,
}

/// <summary>One parsed streaming-transcription event.</summary>
internal readonly record struct MistralSttEvent(MistralSttEventKind Kind, string Text, string? Language);
