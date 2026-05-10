using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Voxa.Pipelines;
using Voxa.Transports.WebSocket;

namespace Voxa.AspNetCore;

/// <summary>
/// Extension methods for mapping a Voxa voice pipeline to an ASP.NET Core endpoint.
/// </summary>
public static class MapVoxaVoiceExtensions
{
    /// <summary>
    /// Map a Voxa voice pipeline at <paramref name="pattern"/>. The supplied
    /// <paramref name="configure"/> callback assembles the per-connection pipeline shape.
    ///
    /// <para>
    /// Per-request behavior:
    /// </para>
    /// <list type="number">
    ///   <item>Validate this is a WebSocket upgrade request (else 400).</item>
    ///   <item>Authorization policies declared via <c>builder.RequireAuthorization(...)</c> run first
    ///       — the route's <c>RequireAuthorization</c> metadata enforces them before this handler.</item>
    ///   <item>Accept the WebSocket.</item>
    ///   <item>Invoke the typed <c>UseWebSocketHello&lt;T&gt;()</c> reader if registered;
    ///       parsed value goes to <c>HttpContext.Items[VoiceHello.HelloMetadataKey]</c>.</item>
    ///   <item>Build the pipeline: <see cref="WebSocketAudioSource"/> → registered processors →
    ///       <see cref="WebSocketAudioSink"/> (with the custom serializer if configured).</item>
    ///   <item>Run via <see cref="PipelineRunner"/>; await completion.</item>
    /// </list>
    /// </summary>
    /// <param name="endpoints">Endpoint route builder (typically the ASP.NET Core <c>WebApplication</c>).</param>
    /// <param name="pattern">Route pattern, e.g. <c>"/voice"</c>.</param>
    /// <param name="configure">Builder configurator. Required.</param>
    /// <returns>The route handler builder so callers can chain further metadata if needed.</returns>
    public static IEndpointConventionBuilder MapVoxaVoice(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        Action<VoicePipelineBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new VoicePipelineBuilder();
        configure(builder);

        var route = endpoints.MapGet(pattern, ctx => HandleAsync(ctx, builder))
            .WithName($"VoxaVoice:{pattern}")
            .WithTags("Voice")
            .WithSummary("Voxa voice pipeline");

        if (builder.AuthPolicies.Count > 0)
        {
            route.RequireAuthorization(builder.AuthPolicies.ToArray());
        }

        if (builder.CorsPolicies.Count > 0)
        {
            foreach (var policy in builder.CorsPolicies)
            {
                route.RequireCors(policy);
            }
        }

        return route;
    }

    private static async Task HandleAsync(HttpContext context, VoicePipelineBuilder builder)
    {
        var ct = context.RequestAborted;
        var logger = context.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("Voxa.AspNetCore.MapVoxaVoice");

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(
                new { error = "WebSocket upgrade required." }, ct).ConfigureAwait(false);
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        logger.LogDebug("Voxa voice WebSocket accepted at {Path}", context.Request.Path);

        try
        {
            // 1. Read typed hello if configured.
            if (builder.HelloReader is { } reader)
            {
                try
                {
                    await reader(context, socket, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Voxa voice: hello reader failed");
                    await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, "Hello envelope rejected", ct).ConfigureAwait(false);
                    return;
                }
            }

            // 2. Build the pipeline.
            var source = new WebSocketAudioSource(socket);
            var sink = new WebSocketAudioSink(socket, builder.CustomSerializer);
            var pipelineBuilder = Pipeline.Build().Source(source);

            foreach (var factory in builder.ProcessorFactories)
            {
                pipelineBuilder.Then(factory(context));
            }

            var pipeline = pipelineBuilder.Sink(sink);

            // 3. Run.
            await using var runner = new PipelineRunner(pipeline, ct);
            await runner.StartAsync(ct: ct).ConfigureAwait(false);

            try
            {
                await runner.WaitAsync().WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Client disconnected — normal.
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* normal */ }
        catch (Exception ex)
        {
            logger.LogError(ex, "Voxa voice: unhandled exception");
            if (socket.State == WebSocketState.Open)
            {
                try
                {
                    await CloseAsync(socket, WebSocketCloseStatus.InternalServerError, "Server error", default).ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort.
                }
            }
        }
    }

    private static async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status, string description, CancellationToken ct)
    {
        if (socket.State == WebSocketState.Open)
        {
            await socket.CloseAsync(status, description, ct).ConfigureAwait(false);
        }
    }
}
