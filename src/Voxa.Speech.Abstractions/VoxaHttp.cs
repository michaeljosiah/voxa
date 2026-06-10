using System.Net;

namespace Voxa.Speech;

/// <summary>
/// Process-wide <see cref="HttpClient"/> for speech providers. One <see cref="SocketsHttpHandler"/>
/// means one connection pool shared by every engine instance across every concurrent session — so
/// after the first request there are no per-session TLS handshakes and no socket exhaustion under
/// load. Engines still accept an injected <see cref="HttpClient"/> (tests, custom policies); this
/// is the default and is never disposed by an engine.
/// </summary>
public static class VoxaHttp
{
    /// <summary>The shared client. Do not dispose.</summary>
    public static HttpClient Shared { get; } = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        EnableMultipleHttp2Connections = true,
        AutomaticDecompression = DecompressionMethods.None,   // audio payloads — don't waste CPU decompressing
        ConnectTimeout = TimeSpan.FromSeconds(5),
    }, disposeHandler: false)
    {
        Timeout = TimeSpan.FromSeconds(100),
    };

    /// <summary>
    /// Pre-establish TCP+TLS to a provider so the session's first synthesis doesn't pay the
    /// handshake (~100–300 ms). Best-effort: any failure (timeout, 4xx, HEAD-not-allowed) is
    /// ignored — the TLS session is established either way, and the real request surfaces real
    /// errors.
    /// </summary>
    public static async Task WarmupAsync(HttpClient client, string baseUrl, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, baseUrl);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var _ = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // Warmup is best-effort.
        }
    }

    /// <summary>
    /// Send a request, retrying exactly ONCE after 150 ms on a transient failure (network error or
    /// HTTP 429 / 5xx). Safe only before any response body has been consumed — used for the initial
    /// TTS request so a single hiccup doesn't drop the turn. Never retries mid-stream.
    /// <paramref name="requestFactory"/> must build a fresh <see cref="HttpRequestMessage"/> each
    /// call (a request and its content cannot be resent).
    /// </summary>
    public static async Task<HttpResponseMessage> SendWithSingleRetryAsync(
        HttpClient client,
        Func<HttpRequestMessage> requestFactory,
        CancellationToken ct)
    {
        try
        {
            using var first = requestFactory();
            var resp = await client.SendAsync(first, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!IsTransient(resp.StatusCode)) return resp;
            resp.Dispose();
        }
        catch (HttpRequestException) { /* fall through to the single retry */ }

        await Task.Delay(150, ct).ConfigureAwait(false);
        using var retry = requestFactory();
        return await client.SendAsync(retry, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        static bool IsTransient(HttpStatusCode code) => (int)code == 429 || (int)code >= 500;
    }
}
