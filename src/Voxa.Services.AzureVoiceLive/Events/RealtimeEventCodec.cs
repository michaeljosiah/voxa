using System.Text.Json;
using System.Text.Json.Serialization;
using Voxa.Frames;

namespace Voxa.Services.AzureVoiceLive.Events;

/// <summary>
/// Builds outgoing Realtime API event JSON and decodes incoming server events into
/// Voxa <see cref="Frame"/>s. The protocol matches Azure OpenAI Realtime (Voice Live is
/// compatible by design).
/// </summary>
internal static class RealtimeEventCodec
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string BuildSessionUpdate(AzureVoiceLiveOptions options)
    {
        var tools = options.Tools.Select(t => new
        {
            type = "function",
            name = t.Name,
            description = t.Description,
            parameters = JsonDocument.Parse(t.ParametersJsonSchema).RootElement.Clone(),
        }).ToArray();

        var payload = new
        {
            type = "session.update",
            session = new
            {
                modalities = new[] { "text", "audio" },
                instructions = options.Instructions,
                voice = options.Voice,
                input_audio_format = "pcm16",
                output_audio_format = "pcm16",
                input_audio_transcription = new { model = "whisper-1" },
                turn_detection = new
                {
                    type = options.TurnDetection.Type,
                    threshold = options.TurnDetection.Threshold,
                    prefix_padding_ms = options.TurnDetection.PrefixPaddingMs,
                    silence_duration_ms = options.TurnDetection.SilenceDurationMs,
                },
                tools = tools.Length > 0 ? tools : null,
                tool_choice = tools.Length > 0 ? "auto" : null,
            },
        };

        return JsonSerializer.Serialize(payload, Json);
    }

    public static string BuildInputAudioBufferAppend(ReadOnlyMemory<byte> pcm)
    {
        var b64 = Convert.ToBase64String(pcm.Span);
        return JsonSerializer.Serialize(new { type = "input_audio_buffer.append", audio = b64 }, Json);
    }

    public static string BuildToolCallOutput(string callId, string output)
    {
        return JsonSerializer.Serialize(new
        {
            type = "conversation.item.create",
            item = new
            {
                type = "function_call_output",
                call_id = callId,
                output,
            },
        }, Json);
    }

    public static string BuildResponseCreate()
        => JsonSerializer.Serialize(new { type = "response.create" }, Json);

    public static string BuildResponseCancel()
        => JsonSerializer.Serialize(new { type = "response.cancel" }, Json);

    /// <summary>
    /// Decode one incoming server event into zero, one, or several <see cref="Frame"/>s. Errors
    /// are returned with <see cref="FrameDirection.Upstream"/> so the runner surfaces them.
    /// </summary>
    public static IEnumerable<Frame> Decode(JsonElement evt, bool botSpeaking, int outputSampleRate)
    {
        if (!evt.TryGetProperty("type", out var typeElement)) yield break;
        var type = typeElement.GetString();

        switch (type)
        {
            case "input_audio_buffer.speech_started":
                yield return new UserStartedSpeakingFrame();
                if (botSpeaking) yield return new InterruptionFrame();
                break;

            case "input_audio_buffer.speech_stopped":
                yield return new UserStoppedSpeakingFrame();
                break;

            case "conversation.item.input_audio_transcription.completed":
            {
                var transcript = evt.TryGetProperty("transcript", out var t) ? (t.GetString() ?? string.Empty) : string.Empty;
                yield return new TranscriptionFrame(transcript, IsFinal: true);
                break;
            }

            case "response.created":
                yield return new BotStartedSpeakingFrame();
                break;

            case "response.done":
                yield return new BotStoppedSpeakingFrame();
                break;

            case "response.audio.delta":
            {
                var b64 = evt.TryGetProperty("delta", out var d) ? (d.GetString() ?? string.Empty) : string.Empty;
                if (b64.Length > 0)
                {
                    byte[] pcm;
                    try { pcm = Convert.FromBase64String(b64); }
                    catch (FormatException) { yield break; }
                    yield return new AudioRawFrame(pcm, outputSampleRate, 1);
                }
                break;
            }

            case "response.audio_transcript.delta":
            {
                var delta = evt.TryGetProperty("delta", out var d) ? (d.GetString() ?? string.Empty) : string.Empty;
                if (delta.Length > 0) yield return new LlmTextChunkFrame(delta);
                break;
            }

            case "response.function_call_arguments.done":
            {
                var callId = evt.TryGetProperty("call_id", out var c) ? (c.GetString() ?? string.Empty) : string.Empty;
                var name = evt.TryGetProperty("name", out var n) ? (n.GetString() ?? string.Empty) : string.Empty;
                var args = evt.TryGetProperty("arguments", out var a) ? (a.GetString() ?? "{}") : "{}";
                yield return new ToolCallRequestFrame(callId, name, args);
                break;
            }

            case "error":
            {
                var msg = "Voice Live error";
                if (evt.TryGetProperty("error", out var err) &&
                    err.TryGetProperty("message", out var m) &&
                    m.GetString() is { } s &&
                    s.Length > 0)
                {
                    msg = s;
                }
                yield return new ErrorFrame(msg) { Direction = FrameDirection.Upstream };
                break;
            }
        }
    }
}
