using System.Net;
using Voxa.Speech;

namespace Voxa.Speech.Abstractions.Tests;

/// <summary>
/// Covers the bounded single-retry helper (VPS-001 WS6.7): a transient failure on the first send
/// is retried exactly once; a success is returned without retry. The retry only wraps the initial
/// request, so a mid-stream failure is structurally never retried.
/// </summary>
public class VoxaHttpTests
{
    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Queue<HttpStatusCode> _codes;
        public int Calls { get; private set; }

        public SequenceHandler(params HttpStatusCode[] codes) => _codes = new Queue<HttpStatusCode>(codes);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var code = _codes.Count > 0 ? _codes.Dequeue() : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(code) { Content = new ByteArrayContent([1, 2]) });
        }
    }

    [Fact]
    public async Task TransientThenSuccess_RetriesOnce_AndReturnsSuccess()
    {
        var handler = new SequenceHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        using var client = new HttpClient(handler);

        using var resp = await VoxaHttp.SendWithSingleRetryAsync(
            client,
            () => new HttpRequestMessage(HttpMethod.Post, "https://example.test/audio/speech"),
            default);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(2, handler.Calls);   // one failed attempt + one retry
    }

    [Fact]
    public async Task Success_DoesNotRetry()
    {
        var handler = new SequenceHandler(HttpStatusCode.OK);
        using var client = new HttpClient(handler);

        using var resp = await VoxaHttp.SendWithSingleRetryAsync(
            client,
            () => new HttpRequestMessage(HttpMethod.Post, "https://example.test/audio/speech"),
            default);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, handler.Calls);   // no retry on success
    }

    [Fact]
    public async Task RateLimited_IsTreatedAsTransient()
    {
        var handler = new SequenceHandler(HttpStatusCode.TooManyRequests, HttpStatusCode.OK);
        using var client = new HttpClient(handler);

        using var resp = await VoxaHttp.SendWithSingleRetryAsync(
            client,
            () => new HttpRequestMessage(HttpMethod.Post, "https://example.test/audio/speech"),
            default);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(2, handler.Calls);
    }
}
