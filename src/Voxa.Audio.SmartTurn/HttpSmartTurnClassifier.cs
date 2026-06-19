using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Voxa.Speech;

namespace Voxa.Audio.SmartTurn;

/// <summary>
/// An <see cref="ISmartTurnClassifier"/> backed by an HTTP endpoint (P0 smart turn). POSTs the recent
/// speech as a WAV and reads a completion verdict from the JSON response.
///
/// <para>
/// Voxa's contract is lenient — the response object may carry a boolean <c>complete</c>/<c>is_complete</c>,
/// an integer <c>prediction</c> (1 = complete), or a float <c>probability</c>/<c>completion_probability</c>
/// compared against <see cref="SmartTurnOptions.Threshold"/>. Self-host a smart-turn model behind this, or
/// point it at a compatible service. It <b>fails "complete"</b> (returns true) on any error or timeout, so a
/// flaky endpoint can never strand the conversation by holding the turn open forever.
/// </para>
/// </summary>
public sealed class HttpSmartTurnClassifier : ISmartTurnClassifier
{
    private readonly SmartTurnOptions _options;
    private readonly HttpClient _http;
    private readonly ILogger _logger;

    public HttpSmartTurnClassifier(SmartTurnOptions options, HttpClient httpClient, ILogger? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? NullLogger.Instance;
        if (string.IsNullOrWhiteSpace(_options.Endpoint))
            throw new ArgumentException("Voxa:SmartTurn:Endpoint is required for the HTTP smart-turn classifier.", nameof(options));
    }

    public async ValueTask<bool> IsTurnCompleteAsync(ReadOnlyMemory<byte> recentSpeechPcm, int sampleRate, CancellationToken ct)
    {
        if (recentSpeechPcm.IsEmpty) return true; // nothing to judge — behave like classic silence
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.TimeoutMs);

            using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
            {
                Content = new ByteArrayContent(BuildWav(recentSpeechPcm.Span, sampleRate))
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("audio/wav") },
                },
            };
            if (!string.IsNullOrEmpty(_options.ApiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return ParseComplete(json, _options.Threshold);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Endpoint error or our own timeout — fail safe to "turn over" so the bot still responds.
            _logger.LogDebug(ex, "Smart-turn endpoint failed; defaulting to turn-complete.");
            return true;
        }
    }

    /// <summary>Parse a completion verdict leniently from the endpoint's JSON (unrecognized shape → complete).</summary>
    internal static bool ParseComplete(string json, double threshold)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return true;

            foreach (var name in (ReadOnlySpan<string>)["complete", "is_complete", "completed"])
                if (root.TryGetProperty(name, out var b) && b.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    return b.GetBoolean();

            if (root.TryGetProperty("prediction", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var pred))
                return pred != 0;

            foreach (var name in (ReadOnlySpan<string>)["probability", "completion_probability", "score"])
                if (root.TryGetProperty(name, out var f) && f.ValueKind == JsonValueKind.Number)
                    return f.GetDouble() >= threshold;

            return true; // unrecognized shape — don't strand the turn
        }
        catch (JsonException)
        {
            return true;
        }
    }

    /// <summary>Wrap 16-bit mono PCM in a minimal 44-byte WAV header.</summary>
    internal static byte[] BuildWav(ReadOnlySpan<byte> pcm, int sampleRate)
    {
        var wav = new byte[44 + pcm.Length];
        var span = wav.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)(36 + pcm.Length));
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 16);                       // fmt chunk size
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..], 1);                        // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(span[22..], 1);                        // mono
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], (uint)(sampleRate * 2));   // byte rate (mono, 16-bit)
        BinaryPrimitives.WriteUInt16LittleEndian(span[32..], 2);                        // block align
        BinaryPrimitives.WriteUInt16LittleEndian(span[34..], 16);                       // bits per sample
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..], (uint)pcm.Length);
        pcm.CopyTo(span[44..]);
        return wav;
    }
}
