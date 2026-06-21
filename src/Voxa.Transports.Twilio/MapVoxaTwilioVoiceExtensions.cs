using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Voxa.AspNetCore;
using Voxa.Frames;
using Voxa.Pipelines;
using Voxa.Transports.Telephony;

namespace Voxa.Transports.Twilio;

/// <summary>
/// Maps a Twilio Media Streams voice endpoint: the TwiML webhook and the media WebSocket route. The media
/// route runs the call through the <b>same</b> composed pipeline as the native <c>UseDefaults()</c> route —
/// telephony is purely an edge skin (a μ-law/8 kHz source and sink). See VTL-001.
/// </summary>
public static class MapVoxaTwilioVoiceExtensions
{
    private const string LoggerCategory = "Voxa.Transports.Twilio";

    /// <summary>
    /// Map a Twilio voice endpoint at <paramref name="pattern"/>:
    /// <list type="number">
    ///   <item><c>POST/GET {pattern}</c> — the webhook: validates <c>X-Twilio-Signature</c> (when enabled) and
    ///     returns TwiML pointing Twilio at the media route.</item>
    ///   <item><c>GET {pattern}/media</c> — the media WebSocket: composes the standard pipeline and runs the call.</item>
    /// </list>
    /// Options bind from <c>Voxa:Telephony:Twilio</c>; <paramref name="configure"/> overrides them in code.
    /// Requires <c>app.UseWebSockets()</c> and a host configured via <c>AddVoxa(configuration)</c>.
    /// </summary>
    /// <returns>The webhook route builder, for further metadata chaining.</returns>
    public static IEndpointConventionBuilder MapVoxaTwilioVoice(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        Action<TwilioTelephonyOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(pattern);

        // Config-capture rule: bind from the IConfiguration registered with the host at map time rather than
        // service-locating it per request. This endpoint is ASP.NET-only, so IConfiguration is always present.
        var options = new TwilioTelephonyOptions();
        endpoints.ServiceProvider.GetService<IConfiguration>()
            ?.GetSection(TwilioTelephonyOptions.SectionName).Bind(options);
        configure?.Invoke(options);

        var mediaPath = $"{pattern.TrimEnd('/')}/media";

        var webhook = endpoints.MapMethods(pattern, ["POST", "GET"], ctx => HandleWebhookAsync(ctx, options, mediaPath))
            .WithName($"VoxaTwilioWebhook:{pattern}")
            .WithTags("Voice")
            .WithSummary("Voxa Twilio voice webhook (TwiML)");

        endpoints.MapGet(mediaPath, HandleMediaAsync)
            .WithName($"VoxaTwilioMedia:{pattern}")
            .WithTags("Voice")
            .WithSummary("Voxa Twilio media stream (WebSocket)");

        return webhook;
    }

    private static async Task HandleWebhookAsync(HttpContext context, TwilioTelephonyOptions options, string mediaPath)
    {
        var ct = context.RequestAborted;
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory);

        if (options.ValidateSignature)
        {
            if (string.IsNullOrEmpty(options.AuthToken))
            {
                // Fail closed: an enabled-but-unconfigured validator must never wave requests through.
                logger.LogError(
                    "Twilio webhook: ValidateSignature is enabled but Voxa:Telephony:Twilio:AuthToken is not set. " +
                    "Set the auth token, or disable validation for local tunnels.");
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsync(
                    "Twilio signature validation is enabled but no auth token is configured.", ct).ConfigureAwait(false);
                return;
            }

            if (!await IsSignatureValidAsync(context, options.AuthToken).ConfigureAwait(false))
            {
                logger.LogWarning("Twilio webhook: invalid or missing X-Twilio-Signature; rejecting request.");
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }

        var wss = ResolveWssUrl(context, options, mediaPath);
        context.Response.ContentType = "text/xml";
        await context.Response.WriteAsync(
            $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Response><Connect><Stream url=\"{wss}\" /></Connect></Response>",
            ct).ConfigureAwait(false);
    }

    private static async Task<bool> IsSignatureValidAsync(HttpContext context, string authToken)
    {
        // Twilio signs the exact URL it requested (scheme + host + path + query). Behind a reverse proxy or
        // tunnel this only matches when forwarded headers are applied; otherwise disable validation (see options).
        var request = context.Request;
        var url = $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path}{request.QueryString}";

        IEnumerable<KeyValuePair<string, string>>? form = null;
        if (HttpMethods.IsPost(request.Method) && request.HasFormContentType)
        {
            var collected = await request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
            form = collected.Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value.ToString()));
        }

        var provided = request.Headers["X-Twilio-Signature"].ToString();
        return TwilioSignatureValidator.IsValid(authToken, url, form, provided);
    }

    private static string ResolveWssUrl(HttpContext context, TwilioTelephonyOptions options, string mediaPath)
    {
        if (!string.IsNullOrEmpty(options.PublicWssBaseUrl))
            return $"{options.PublicWssBaseUrl.TrimEnd('/')}{mediaPath}";
        return $"wss://{context.Request.Host}{context.Request.PathBase}{mediaPath}";
    }

    private static async Task HandleMediaAsync(HttpContext context)
    {
        var ct = context.RequestAborted;
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory);

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "WebSocket upgrade required." }, ct).ConfigureAwait(false);
            return;
        }

        // Compose BEFORE accepting the WebSocket so a configuration error (missing provider) surfaces as a
        // proper HTTP 500 rather than an accepted-then-aborted socket. Same as the native route.
        ComposedVoice composed;
        try
        {
            composed = context.RequestServices.GetRequiredService<DefaultVoicePipelineComposer>().Compose(context);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Twilio media: pipeline composition failed");
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(
                new { error = "Voice pipeline configuration error. See server logs." }, ct).ConfigureAwait(false);
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        logger.LogDebug("Twilio media WebSocket accepted at {Path}", context.Request.Path);

        try
        {
            // One codec shared by the source and sink: the source's read loop captures the streamSid from the
            // Twilio `start` event; the sink reads it to address outbound messages.
            var codec = new TwilioMediaStreamCodec();
            var source = new TelephonyMediaStreamSource(socket, codec, composed.InputSampleRate);
            var sink = new TelephonyMediaStreamSink(socket, codec, composed.OutputSampleRate);

            var pipelineBuilder = Pipeline.Build().Source(source);
            foreach (var factory in composed.Parts)
                pipelineBuilder.Then(factory(context.RequestServices));
            var pipeline = pipelineBuilder.Sink(sink);

            await using var runner = new PipelineRunner(pipeline, ct);
            await runner.StartAsync(ct: ct).ConfigureAwait(false);

            // Announce the session rates right after start (parity with the native route + diagnostics). The
            // telephony sink ignores this frame; it carries no Twilio wire message.
            await source.IngestAsync(
                new SessionInfoFrame(composed.InputSampleRate, composed.OutputSampleRate), ct).ConfigureAwait(false);

            try
            {
                await runner.WaitAsync().WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Caller hung up / Twilio closed the stream — normal.
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* normal */ }
        catch (Exception ex)
        {
            logger.LogError(ex, "Twilio media: unhandled exception");
        }
    }
}
