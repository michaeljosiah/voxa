using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Voxa.Speech.AssemblyAI;

/// <summary>
/// AssemblyAI Universal-Streaming (v3) <see cref="ISpeechToTextEngine"/> over <see cref="WebSocketSttEngine"/>.
/// Each <c>Turn</c> message carries the cumulative turn transcript; <c>end_of_turn</c> marks it finalized. The
/// base streams interims for display and emits one VAD-gated final per utterance (see <see cref="WebSocketSttEngine"/>).
/// </summary>
public sealed class AssemblyAISttEngine : WebSocketSttEngine
{
    private static readonly byte[] TerminateFrame = Encoding.UTF8.GetBytes("{\"type\":\"Terminate\"}");
    private readonly AssemblyAIOptions _options;

    public AssemblyAISttEngine(AssemblyAIOptions options)
        => _options = options ?? throw new ArgumentNullException(nameof(options));

    protected override Uri BuildEndpoint()
        => new($"{_options.ApiBaseUrl.TrimEnd('/')}?sample_rate={_options.InputSampleRate}" +
               $"&encoding=pcm_s16le&format_turns={(_options.FormatTurns ? "true" : "false")}");

    protected override void ConfigureConnect(ClientWebSocket ws)
        => ws.Options.SetRequestHeader("Authorization", _options.ApiKey);

    protected override ReadOnlyMemory<byte>? BuildCloseMessage() => TerminateFrame;

    protected override IEnumerable<SttFragment> ParseMessage(string message) => Parse(message);

    /// <summary>
    /// Pure parser (testable). Only a <c>Turn</c> message with a non-empty transcript yields a fragment;
    /// the transcript is cumulative for the turn, so <c>end_of_turn:true</c> ⇒ a locked segment, else interim.
    /// <c>Begin</c> / <c>Termination</c> messages yield nothing.
    /// </summary>
    internal static IEnumerable<SttFragment> Parse(string message)
    {
        using var doc = JsonDocument.Parse(message);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) yield break;
        if (!root.TryGetProperty("type", out var type) || type.GetString() != "Turn") yield break;

        var transcript = root.TryGetProperty("transcript", out var tr) ? tr.GetString() ?? string.Empty : string.Empty;
        if (transcript.Length == 0) yield break;

        bool endOfTurn = root.TryGetProperty("end_of_turn", out var e) && e.ValueKind == JsonValueKind.True;
        yield return new SttFragment(transcript, endOfTurn);
    }
}
