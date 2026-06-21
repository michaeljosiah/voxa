using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Voxa.Speech.Deepgram;

/// <summary>
/// Deepgram streaming <see cref="ISpeechToTextEngine"/> over <see cref="WebSocketSttEngine"/>: opens a
/// <c>wss://api.deepgram.com/v1/listen</c> socket, streams <c>linear16</c> PCM, and parses incremental
/// <c>Results</c> messages. The base accumulates locked (<c>is_final</c>) segments and emits one VAD-gated
/// final per utterance — see <see cref="WebSocketSttEngine"/> for the turn-integration rationale.
/// </summary>
public sealed class DeepgramSttEngine : WebSocketSttEngine
{
    private static readonly byte[] CloseStreamFrame = Encoding.UTF8.GetBytes("{\"type\":\"CloseStream\"}");
    private readonly DeepgramOptions _options;

    public DeepgramSttEngine(DeepgramOptions options)
        => _options = options ?? throw new ArgumentNullException(nameof(options));

    protected override string? Language => _options.Language;

    protected override Uri BuildEndpoint()
    {
        var q = $"?model={Uri.EscapeDataString(_options.Model)}&encoding=linear16" +
                $"&sample_rate={_options.InputSampleRate}&channels=1&interim_results=true&smart_format=true";
        if (!string.IsNullOrEmpty(_options.Language))
            q += $"&language={Uri.EscapeDataString(_options.Language)}";
        return new Uri(_options.ApiBaseUrl.TrimEnd('/') + q);
    }

    protected override void ConfigureConnect(ClientWebSocket ws)
        => ws.Options.SetRequestHeader("Authorization", $"Token {_options.ApiKey}");

    // Deepgram flushes and closes cleanly when it receives a CloseStream control message.
    protected override ReadOnlyMemory<byte>? BuildCloseMessage() => CloseStreamFrame;

    protected override IEnumerable<SttFragment> ParseMessage(string message) => Parse(message);

    /// <summary>
    /// Pure parser (testable) for one Deepgram streaming message. Only a <c>Results</c> message with a
    /// non-empty transcript yields a fragment: <c>is_final:true</c> ⇒ a locked segment, otherwise an interim.
    /// Other message types (<c>Metadata</c>, <c>UtteranceEnd</c>, <c>SpeechStarted</c>) yield nothing.
    /// </summary>
    internal static IEnumerable<SttFragment> Parse(string message)
    {
        using var doc = JsonDocument.Parse(message);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) yield break;
        if (!root.TryGetProperty("type", out var type) || type.GetString() != "Results") yield break;
        if (!root.TryGetProperty("channel", out var channel)
            || !channel.TryGetProperty("alternatives", out var alts)
            || alts.ValueKind != JsonValueKind.Array || alts.GetArrayLength() == 0) yield break;

        var transcript = alts[0].TryGetProperty("transcript", out var tr) ? tr.GetString() ?? string.Empty : string.Empty;
        if (transcript.Length == 0) yield break;

        bool isFinal = root.TryGetProperty("is_final", out var f) && f.ValueKind == JsonValueKind.True;
        yield return new SttFragment(transcript, isFinal);
    }
}
