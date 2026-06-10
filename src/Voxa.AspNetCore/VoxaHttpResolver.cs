using Voxa.Speech;

namespace Voxa.AspNetCore;

/// <summary>
/// Resolves the <see cref="HttpClient"/> handed to speech engines created by descriptors.
/// Uses <see cref="IHttpClientFactory"/>'s named client when available (proxies, custom handlers,
/// Polly — whatever the host configured); falls back to <see cref="VoxaHttp.Shared"/> otherwise.
/// Factory-produced clients are cheap wrappers over pooled handlers — creating one per
/// connection is the intended usage pattern.
/// </summary>
public sealed class VoxaHttpResolver : IVoxaHttpClientProvider
{
    public const string ClientName = "Voxa";
    private readonly IHttpClientFactory? _factory;

    public VoxaHttpResolver(IHttpClientFactory? factory = null) => _factory = factory;

    public HttpClient? Resolve() => _factory?.CreateClient(ClientName) ?? VoxaHttp.Shared;
}
