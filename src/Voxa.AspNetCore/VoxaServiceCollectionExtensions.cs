using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Voxa.Speech;

namespace Voxa.AspNetCore;

/// <summary>
/// Fluent builder for per-package (à-la-carte) provider registration.
/// </summary>
public sealed class VoxaBuilder
{
    public IServiceCollection Services { get; }
    internal VoxaProviderRegistry Registry { get; }

    internal VoxaBuilder(IServiceCollection services, VoxaProviderRegistry registry)
    {
        Services = services;
        Registry = registry;
    }

    public VoxaBuilder AddProvider(VoxaSttDescriptor stt)  { Registry.Add(stt);  return this; }
    public VoxaBuilder AddProvider(VoxaTtsDescriptor tts)  { Registry.Add(tts);  return this; }
    public VoxaBuilder AddProvider(VoxaVadDescriptor vad)  { Registry.Add(vad);  return this; }
    public VoxaBuilder AddProvider(VoxaAecDescriptor aec)  { Registry.Add(aec);  return this; } // VRT-003: external Voxa.Audio.Aec.* packages
    public VoxaBuilder AddProvider(VoxaEnhancerDescriptor enhancer) { Registry.Add(enhancer); return this; } // VLS-004: external Voxa.Audio.Enhance
}

public static class VoxaServiceCollectionExtensions
{
    /// <summary>
    /// Register Voxa options, validation, the provider registry, and all hosting infrastructure.
    /// À-la-carte hosts use the <paramref name="configure"/> callback to register only the
    /// provider packages they have installed. The Voxa meta-package provides a zero-arg overload
    /// that pre-registers all built-in providers (beginners never see this delegate).
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="configuration">Application configuration; the <c>Voxa</c> section is bound.</param>
    /// <param name="configure">Required — à-la-carte provider registration. Cannot be null.
    /// (Making this required avoids overload-resolution ambiguity with the meta-package's
    /// two-param overload.)</param>
    public static IServiceCollection AddVoxa(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<VoxaBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(configure);

        // Idempotent: a second AddVoxa call merges its descriptors into the existing registry
        // instead of replacing it (which would silently discard the first call's providers)
        // and skips re-registering the infrastructure (avoiding a duplicate hosted service).
        var existingRegistry = services
            .FirstOrDefault(d => d.ServiceType == typeof(VoxaProviderRegistry))
            ?.ImplementationInstance as VoxaProviderRegistry;
        if (existingRegistry is not null)
        {
            configure(new VoxaBuilder(services, existingRegistry));
            return services;
        }

        var builder = new VoxaBuilder(services, new VoxaProviderRegistry());
        configure(builder);

        services.AddSingleton(builder.Registry);
        services.AddOptions<VoxaOptions>()
            .Bind(configuration.GetSection(VoxaOptions.SectionName))
            .ValidateOnStart();
        // Validator and composer capture the SAME configuration instance the options were bound
        // from, rather than resolving IConfiguration from DI — which may be absent (plain
        // ServiceCollection in tests) or a different root than the one passed here.
        services.AddSingleton<IValidateOptions<VoxaOptions>>(sp =>
            new VoxaOptionsValidator(sp.GetRequiredService<VoxaProviderRegistry>(), configuration));
        services.AddSingleton<VoxaTuningResolver>();
        services.AddSingleton(sp => new DefaultVoicePipelineComposer(
            sp.GetRequiredService<IOptions<VoxaOptions>>(),
            sp.GetRequiredService<VoxaProviderRegistry>(),
            sp.GetRequiredService<VoxaTuningResolver>(),
            configuration,
            sp.GetRequiredService<ILogger<DefaultVoicePipelineComposer>>()));
        // Per-session diagnostics hub (VST-001 WS0). Scoped: one per WebSocket connection on a
        // server, one per Talk session in Voxa Studio. Registered unconditionally (it is inert
        // until someone subscribes); taps are only composed in when Voxa:Diagnostics:Enabled.
        services.AddScoped(sp => new Voxa.Diagnostics.VoxaDiagnosticsHub(
            sp.GetRequiredService<IOptions<VoxaOptions>>().Value.Diagnostics.ChannelCapacity));

        services.AddSingleton<VoxaHttpResolver>();
        services.AddSingleton<IVoxaHttpClientProvider>(sp => sp.GetRequiredService<VoxaHttpResolver>());
        services.AddSingleton<VoxaDefaultsGuard>();
        services.AddHostedService(sp => sp.GetRequiredService<VoxaDefaultsGuard>());

        // Named HttpClient mirroring VoxaHttp.Shared's handler settings.
        // Hosts override: services.AddHttpClient(VoxaHttpResolver.ClientName).Configure...
        services.AddHttpClient(VoxaHttpResolver.ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionLifetime   = TimeSpan.FromMinutes(5),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                EnableMultipleHttp2Connections = true,
                AutomaticDecompression     = DecompressionMethods.None,
                ConnectTimeout             = TimeSpan.FromSeconds(5),
            })
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(100));

        return services;
    }
}
