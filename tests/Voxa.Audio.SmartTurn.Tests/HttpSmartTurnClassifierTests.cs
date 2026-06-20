using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Voxa.Speech;

namespace Voxa.Audio.SmartTurn.Tests;

/// <summary>
/// The HTTP smart-turn classifier: lenient JSON verdict parsing, a valid WAV body, fail-safe-to-complete
/// on endpoint errors, and the opt-in DI registration. No real network — a stub HttpMessageHandler.
/// </summary>
public class HttpSmartTurnClassifierTests
{
    private sealed class StubHandler(HttpStatusCode status, string body, Action<HttpRequestMessage>? onRequest = null)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            onRequest?.Invoke(request);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("boom");
    }

    private sealed class FakeHttpClientProvider(HttpClient client) : IVoxaHttpClientProvider
    {
        public HttpClient? Resolve() => client;
    }

    private static HttpSmartTurnClassifier Classifier(HttpMessageHandler handler)
        => new(new SmartTurnOptions { Endpoint = "http://localhost/predict" }, new HttpClient(handler));

    [Theory]
    [InlineData("{\"complete\":true}", true)]
    [InlineData("{\"complete\":false}", false)]
    [InlineData("{\"is_complete\":false}", false)]
    [InlineData("{\"prediction\":1}", true)]
    [InlineData("{\"prediction\":0}", false)]
    [InlineData("{\"probability\":0.9}", true)]    // ≥ 0.5 threshold
    [InlineData("{\"probability\":0.2}", false)]   // < 0.5 threshold
    [InlineData("{\"unknown\":1}", true)]          // unrecognized shape → don't strand the turn
    [InlineData("not json at all", true)]
    public void ParseComplete_Reads_The_Verdict_Leniently(string json, bool expected)
        => Assert.Equal(expected, HttpSmartTurnClassifier.ParseComplete(json, 0.5));

    [Fact]
    public async Task Posts_A_Wav_And_Returns_The_Endpoint_Verdict()
    {
        HttpRequestMessage? seen = null;
        var c = Classifier(new StubHandler(HttpStatusCode.OK, "{\"complete\":false}", r => seen = r));

        var complete = await c.IsTurnCompleteAsync(new byte[320], 16000, CancellationToken.None);

        Assert.False(complete);
        Assert.NotNull(seen);
        Assert.Equal(HttpMethod.Post, seen!.Method);
        Assert.Equal("audio/wav", seen.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Fails_Complete_When_The_Endpoint_Errors()
    {
        var c = Classifier(new ThrowingHandler());
        Assert.True(await c.IsTurnCompleteAsync(new byte[320], 16000, CancellationToken.None));
    }

    [Fact]
    public async Task Empty_Audio_Is_Treated_As_Turn_Complete()
    {
        var c = Classifier(new StubHandler(HttpStatusCode.OK, "{\"complete\":false}"));
        Assert.True(await c.IsTurnCompleteAsync(ReadOnlyMemory<byte>.Empty, 16000, CancellationToken.None));
    }

    [Fact]
    public void AddVoxaSmartTurn_Registers_The_Http_Classifier_When_Configured()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Voxa:SmartTurn:Provider"] = "Http",
            ["Voxa:SmartTurn:Endpoint"] = "http://localhost/predict",
        }).Build();

        using var sp = new ServiceCollection().AddVoxaSmartTurn(config).BuildServiceProvider();
        Assert.IsType<HttpSmartTurnClassifier>(sp.GetService<ISmartTurnClassifier>());
    }

    [Fact] // Codex P2: the smart-turn endpoint must honor a host-customized Voxa HTTP client, not pin VoxaHttp.Shared.
    public async Task AddVoxaSmartTurn_Http_Uses_The_Host_Configured_HttpClient()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Voxa:SmartTurn:Provider"] = "Http",
            ["Voxa:SmartTurn:Endpoint"] = "http://localhost/predict",
        }).Build();

        var usedHostClient = false;
        var hostClient = new HttpClient(new StubHandler(HttpStatusCode.OK, "{\"complete\":true}", _ => usedHostClient = true));

        using var sp = new ServiceCollection()
            .AddSingleton<IVoxaHttpClientProvider>(new FakeHttpClientProvider(hostClient))
            .AddVoxaSmartTurn(config)
            .BuildServiceProvider();

        await sp.GetRequiredService<ISmartTurnClassifier>()
            .IsTurnCompleteAsync(new byte[320], 16000, CancellationToken.None);

        Assert.True(usedHostClient); // the POST went through the resolved client, not the shared fallback
    }

    [Fact]
    public void AddVoxaSmartTurn_Registers_Nothing_When_Not_Configured()
    {
        using var sp = new ServiceCollection().AddVoxaSmartTurn(new ConfigurationBuilder().Build()).BuildServiceProvider();
        Assert.Null(sp.GetService<ISmartTurnClassifier>());
    }

    [Fact]
    public void AddVoxaSmartTurn_Throws_When_Http_Without_Endpoint()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Voxa:SmartTurn:Provider"] = "Http",
        }).Build();

        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddVoxaSmartTurn(config));
    }

    [Fact] // Codex P2: a typo'd provider must fail fast, not silently run silence-only.
    public void AddVoxaSmartTurn_Throws_On_An_Unknown_Provider()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Voxa:SmartTurn:Provider"] = "Htp",
        }).Build();

        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddVoxaSmartTurn(config));
    }

    [Fact] // "None" is the explicit opt-out (Studio's off-override) — a no-op, never a throw.
    public void AddVoxaSmartTurn_Ignores_A_None_Provider()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Voxa:SmartTurn:Provider"] = "None",
        }).Build();

        using var sp = new ServiceCollection().AddVoxaSmartTurn(config).BuildServiceProvider();
        Assert.Null(sp.GetService<ISmartTurnClassifier>());
    }
}
