using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Voxa.AspNetCore;

/// <summary>
/// Fluent handle returned by <c>MapVoxaVoice(pattern)</c>. Call <see cref="UseDefaults()"/> to
/// get the standard pipeline, or <see cref="Use"/> to supply a custom one. Implements
/// <see cref="IEndpointConventionBuilder"/> so standard route metadata chaining (authorization,
/// CORS, OpenAPI) works.
/// </summary>
public sealed class VoxaVoiceRoute : IEndpointConventionBuilder
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IEndpointConventionBuilder _routeBuilder;

    private bool _useDefaults;
    private Action<HttpContext, VoicePipelineBuilder>? _customConfigure;

    internal VoxaVoiceRoute(IEndpointRouteBuilder endpoints, string pattern)
    {
        _serviceProvider = endpoints.ServiceProvider;

        // Route is registered eagerly so it is always present in the endpoint table even if
        // the caller forgets to call UseDefaults() or Use(). The factory lambda reads _useDefaults
        // and _customConfigure at request time (after startup methods have returned).
        var route = this;
        _routeBuilder = MapVoxaVoiceExtensions.MapVoxaVoiceInternal(
            endpoints,
            pattern,
            ctx =>
            {
                // A bare MapVoxaVoice(pattern) with no UseDefaults()/Use() would create a
                // source→sink pipeline that echoes client audio straight back. Fail loudly
                // instead — this is always a forgotten configuration call.
                if (!route._useDefaults && route._customConfigure is null)
                    throw new InvalidOperationException(
                        $"MapVoxaVoice(\"{pattern}\") was mapped but never configured. " +
                        "Chain .UseDefaults() for the standard pipeline or .Use(...) for a custom one.");

                var pipelineBuilder = new VoicePipelineBuilder();

                if (route._useDefaults)
                {
                    var composer = ctx.RequestServices.GetRequiredService<DefaultVoicePipelineComposer>();
                    var composed = composer.Compose(ctx);
                    foreach (var factory in composed.Parts)
                        pipelineBuilder.UseProcessor(factory);
                    pipelineBuilder.SessionInputSampleRate = composed.InputSampleRate;
                    pipelineBuilder.SessionOutputSampleRate = composed.OutputSampleRate;
                }

                route._customConfigure?.Invoke(ctx, pipelineBuilder);

                // Endpoint metadata cannot be applied from here: this callback runs per request,
                // AFTER routing has selected the endpoint and evaluated its authorization/CORS
                // metadata. Silently ignoring the call would leave an endpoint the caller believes
                // is protected reachable without the policy — fail closed instead (HTTP 500).
                if (pipelineBuilder.AuthPolicies.Count > 0 || pipelineBuilder.CorsPolicies.Count > 0)
                    throw new InvalidOperationException(
                        $"RequireAuthorization()/RequireCors() was called on the VoicePipelineBuilder inside " +
                        $"MapVoxaVoice(\"{pattern}\").Use(...). On this fluent route the callback runs per " +
                        "request, after endpoint metadata has already been evaluated, so the policy would " +
                        "never be enforced. Chain it on the route instead: " +
                        $"MapVoxaVoice(\"{pattern}\").RequireAuthorization(...) / .RequireCors(...).");

                return pipelineBuilder;
            });
    }

    /// <summary>
    /// Compose the standard pipeline: VAD → STT → TranscriptionFilter → agent (with
    /// built-in conversation memory) → SentenceAggregator → TTS, all wired from the
    /// registered providers and active latency profile. Idempotent.
    /// </summary>
    public VoxaVoiceRoute UseDefaults()
    {
        _useDefaults = true;
        _serviceProvider.GetService<VoxaDefaultsGuard>()?.Arm();
        return this;
    }

    /// <summary>
    /// Configure the pipeline using a per-connection callback that has access to the full
    /// <see cref="HttpContext"/> (DI, claims, hello envelope, etc.).
    /// Can be chained after <see cref="UseDefaults"/> to append extra processors.
    /// Multiple calls compose: callbacks run in registration order.
    /// </summary>
    public VoxaVoiceRoute Use(Action<HttpContext, VoicePipelineBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _customConfigure += configure;
        return this;
    }

    /// <summary>Apply authorization policies to the mapped endpoint.</summary>
    public VoxaVoiceRoute RequireAuthorization(params string[] policies)
    {
        _routeBuilder.RequireAuthorization(policies);
        return this;
    }

    /// <summary>Apply CORS policies to the mapped endpoint.</summary>
    public VoxaVoiceRoute RequireCors(params string[] policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        foreach (var policy in policies)
        {
            _routeBuilder.RequireCors(policy);
        }
        return this;
    }

    public void Add(Action<EndpointBuilder> convention) => _routeBuilder.Add(convention);
}
