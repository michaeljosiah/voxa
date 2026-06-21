using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Voxa.Speech.Speechmatics;

/// <summary>
/// Speechmatics real-time (v2) streaming <see cref="ISpeechToTextEngine"/> over <see cref="WebSocketSttEngine"/>.
/// After connecting it sends a <c>StartRecognition</c> handshake (audio format + transcription config), then
/// parses <c>AddPartialTranscript</c> (interim) / <c>AddTranscript</c> (locked segment) messages, and closes with
/// <c>EndOfStream</c> carrying the audio-chunk sequence number. Locked segments accumulate into one VAD-gated
/// final per utterance (see <see cref="WebSocketSttEngine"/>).
/// </summary>
public sealed class SpeechmaticsSttEngine : WebSocketSttEngine
{
    private readonly SpeechmaticsOptions _options;

    public SpeechmaticsSttEngine(SpeechmaticsOptions options)
        => _options = options ?? throw new ArgumentNullException(nameof(options));

    protected override string? Language => _options.Language;

    protected override Uri BuildEndpoint() => new(_options.ApiBaseUrl);

    protected override void ConfigureConnect(ClientWebSocket ws)
        => ws.Options.SetRequestHeader("Authorization", $"Bearer {_options.ApiKey}");

    protected override async Task OnConnectedAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var start = JsonSerializer.Serialize(new
        {
            message = "StartRecognition",
            audio_format = new { type = "raw", encoding = "pcm_s16le", sample_rate = _options.InputSampleRate },
            transcription_config = new { language = _options.Language, enable_partials = true, max_delay = 1.0 },
        });
        await ws.SendAsync(Encoding.UTF8.GetBytes(start), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

        // Speechmatics requires waiting for RecognitionStarted before sending audio; block here (which blocks
        // StartAsync, and therefore the first WriteAudioAsync) so the server doesn't reject early AddAudio frames.
        await AwaitRecognitionStartedAsync(ws, ct).ConfigureAwait(false);
    }

    private static async Task AwaitRecognitionStartedAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8 * 1024];
        using var msg = new MemoryStream();
        for (var seen = 0; seen < 50 && ws.State == WebSocketState.Open;)
        {
            var result = await ws.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return;
            msg.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;

            var kind = MessageType(Encoding.UTF8.GetString(msg.GetBuffer(), 0, (int)msg.Length));
            msg.SetLength(0);
            seen++;
            if (kind == "RecognitionStarted") return;
            if (kind == "Error") throw new InvalidOperationException("Speechmatics rejected StartRecognition.");
        }
    }

    private static string? MessageType(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
        }
        catch { return null; }
    }

    // Speechmatics ends a session with EndOfStream carrying the count of audio chunks sent.
    protected override ReadOnlyMemory<byte>? BuildCloseMessage()
        => Encoding.UTF8.GetBytes($"{{\"message\":\"EndOfStream\",\"last_seq_no\":{AudioChunksSent}}}");

    protected override IEnumerable<SttFragment> ParseMessage(string message) => Parse(message);

    /// <summary>
    /// Pure parser (testable). <c>AddPartialTranscript</c> ⇒ interim, <c>AddTranscript</c> ⇒ a locked segment;
    /// the text is <c>metadata.transcript</c>. Other messages (<c>RecognitionStarted</c>, <c>AudioAdded</c>, …) yield nothing.
    /// </summary>
    internal static IEnumerable<SttFragment> Parse(string message)
    {
        using var doc = JsonDocument.Parse(message);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) yield break;
        var kind = root.TryGetProperty("message", out var m) ? m.GetString() : null;
        if (kind != "AddTranscript" && kind != "AddPartialTranscript") yield break;

        var text = root.TryGetProperty("metadata", out var meta) && meta.TryGetProperty("transcript", out var t)
            ? t.GetString() ?? string.Empty : string.Empty;
        if (text.Length == 0) yield break;

        yield return new SttFragment(text, IsSegmentFinal: kind == "AddTranscript");
    }
}
