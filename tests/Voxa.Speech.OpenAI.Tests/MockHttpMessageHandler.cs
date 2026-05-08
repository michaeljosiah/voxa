using System.Net;
using System.Net.Http.Headers;

namespace Voxa.Speech.OpenAI.Tests;

internal sealed record CapturedRequest(
    HttpMethod Method,
    Uri RequestUri,
    HttpRequestHeaders Headers,
    HttpContentHeaders? ContentHeaders,
    string? BodyAsString,
    Type? ContentType);

internal sealed class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly List<CapturedRequest> _captured = new();
    private readonly object _lock = new();

    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
        _ => new HttpResponseMessage(HttpStatusCode.OK);

    public IReadOnlyList<CapturedRequest> Captured
    {
        get { lock (_lock) return _captured.ToList(); }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Snapshot the request before the caller disposes it — many engines wrap their
        // HttpRequestMessage in `using`, which then disposes the inner content too.
        string? body = null;
        Type? contentType = null;
        if (request.Content is not null)
        {
            contentType = request.Content.GetType();
            if (contentType != typeof(MultipartFormDataContent))
            {
                body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        var snapshot = new CapturedRequest(
            request.Method,
            request.RequestUri!,
            request.Headers,
            request.Content?.Headers,
            body,
            contentType);

        lock (_lock) _captured.Add(snapshot);
        return Respond(request);
    }
}
