using System.Net.WebSockets;
using Voxa.Pipelines;
using Voxa.Services.AzureVoiceLive;
using Voxa.Transports.WebSocket;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(60) });

app.MapGet("/", () => Results.Text(
    """
    Voxa Voice Live sample.

    Open a WebSocket to /voice and stream 16-bit PCM @ 24 kHz mono in binary frames.
    Server sends:
      - binary: PCM audio (assistant response)
      - text JSON: transcription/text/toolCall/speaking/interruption/error/end envelopes

    Configure AzureVoiceLive:Endpoint and ApiKey in appsettings.json or environment.
    """));

app.Map("/voice", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("WebSocket required");
        return;
    }

    var endpoint = builder.Configuration["AzureVoiceLive:Endpoint"];
    var apiKey = builder.Configuration["AzureVoiceLive:ApiKey"];
    var model = builder.Configuration["AzureVoiceLive:Model"] ?? "gpt-realtime-mini";
    var voice = builder.Configuration["AzureVoiceLive:Voice"] ?? "alloy";
    var instructions = builder.Configuration["AzureVoiceLive:Instructions"]
        ?? "You are a friendly voice assistant. Keep responses brief.";

    if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(apiKey))
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsync(
            "AzureVoiceLive:Endpoint and AzureVoiceLive:ApiKey must be configured.");
        return;
    }

    using var ws = await context.WebSockets.AcceptWebSocketAsync();

    var options = new AzureVoiceLiveOptions
    {
        Endpoint = new Uri(endpoint),
        ApiKey = apiKey,
        Model = model,
        Voice = voice,
        Instructions = instructions,
    };

    var pipeline = Pipeline.Build()
        .Source(new WebSocketAudioSource(ws))
        .Then(new AzureVoiceLiveProcessor(options))
        .Sink(new WebSocketAudioSink(ws));

    await using var runner = new PipelineRunner(pipeline, context.RequestAborted);

    try
    {
        await runner.StartAsync(ct: context.RequestAborted);
        await runner.WaitAsync().WaitAsync(context.RequestAborted);
    }
    catch (OperationCanceledException) { /* client disconnected */ }
    catch (PipelineFailedException ex)
    {
        app.Logger.LogError(ex, "Voxa pipeline failed");
    }

    if (ws.State == WebSocketState.Open)
    {
        try
        {
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        }
        catch { /* best-effort */ }
    }
});

app.Run();
