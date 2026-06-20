using Voxa.Speech;

namespace Voxa.Speech.Abstractions.Tests;

/// <summary>
/// The shared <c>IServiceProvider.ResolveHttpClient()</c> helper (CQ-013) that every provider descriptor now
/// uses instead of its own copy of the resolver expression: it returns the host-configured client, or null
/// when no <see cref="IVoxaHttpClientProvider"/> is registered (so engines fall back to <c>VoxaHttp.Shared</c>).
/// </summary>
public class VoxaHttpClientResolutionTests
{
    private sealed class StubServiceProvider(object? service) : IServiceProvider
    {
        public object? GetService(Type serviceType) => service;
    }

    private sealed class FakeHttpClientProvider(HttpClient client) : IVoxaHttpClientProvider
    {
        public HttpClient? Resolve() => client;
    }

    [Fact]
    public void Returns_Null_When_No_Provider_Is_Registered()
    {
        Assert.Null(new StubServiceProvider(null).ResolveHttpClient());
    }

    [Fact]
    public void Returns_The_Registered_Providers_Client()
    {
        using var client = new HttpClient();
        var sp = new StubServiceProvider(new FakeHttpClientProvider(client));

        Assert.Same(client, sp.ResolveHttpClient());
    }
}
