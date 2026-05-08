using System.Net.WebSockets;
using Voxa.Frames;
using Voxa.Pipelines;
using Voxa.Processors;
using Voxa.Services.AzureVoiceLive;
using Voxa.Speech;
using Voxa.Speech.Azure;
using Voxa.Speech.ElevenLabs;
using Voxa.Speech.Mistral;
using Voxa.Speech.OpenAI;
using Voxa.Transports.WebSocket;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(60) });

app.MapGet("/", () => Results.Text(
    """
    Voxa multi-vendor sample.

    Open a WebSocket to one of these endpoints, stream 16-bit PCM @ 24 kHz mono:

      /voice/voice-live            Azure Voice Live composite (STT+LLM+TTS+VAD in one node)
      /voice/azure                 Azure Speech STT  → echo → Azure Speech TTS
      /voice/openai                OpenAI Whisper STT → echo → OpenAI TTS
      /voice/azure-elevenlabs      Azure Speech STT  → echo → ElevenLabs TTS
      /voice/azure-mistral         Azure Speech STT  → echo → Mistral TTS

    Each endpoint reads its config from appsettings.json. Configure only the
    vendors whose endpoints you intend to call. The /voice/voice-live route
    runs a full LLM-driven agent; the others are STT→echo→TTS loopbacks
    that demonstrate the vendor-swap story (drop in MicrosoftAgentsProcessor
    in place of EchoTranscriptionProcessor for a real granular agent path).
    """));

app.Map("/voice/voice-live", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }

    var endpoint = builder.Configuration["AzureVoiceLive:Endpoint"];
    var apiKey = builder.Configuration["AzureVoiceLive:ApiKey"];
    if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(apiKey))
    {
        ctx.Response.StatusCode = 500;
        await ctx.Response.WriteAsync("AzureVoiceLive:Endpoint and AzureVoiceLive:ApiKey must be configured.");
        return;
    }

    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    var options = new AzureVoiceLiveOptions
    {
        Endpoint = new Uri(endpoint),
        ApiKey = apiKey,
        Model = builder.Configuration["AzureVoiceLive:Model"] ?? "gpt-realtime-mini",
        Voice = builder.Configuration["AzureVoiceLive:Voice"] ?? "alloy",
        Instructions = builder.Configuration["AzureVoiceLive:Instructions"]
            ?? "You are a friendly voice assistant. Keep responses brief.",
    };

    var pipeline = Pipeline.Build()
        .Source(new WebSocketAudioSource(ws))
        .Then(new AzureVoiceLiveProcessor(options))
        .Sink(new WebSocketAudioSink(ws));

    await RunAsync(pipeline, ws, ctx, app.Logger);
});

app.Map("/voice/azure", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    var azure = ReadAzureSpeechOptions(builder.Configuration);
    if (azure is null) { await Missing(ctx, "AzureSpeech"); return; }

    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    var pipeline = Pipeline.Build()
        .Source(new WebSocketAudioSource(ws, new WebSocketAudioOptions { InputSampleRate = azure.InputSampleRate }))
        .Then(AzureSpeech.StreamingTranscription(azure))
        .Then(new EchoTranscriptionProcessor())
        .Then(AzureSpeech.Synthesis(azure))
        .Sink(new WebSocketAudioSink(ws));

    await RunAsync(pipeline, ws, ctx, app.Logger);
});

app.Map("/voice/openai", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    var openai = ReadOpenAIOptions(builder.Configuration);
    if (openai is null) { await Missing(ctx, "OpenAI"); return; }

    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    var pipeline = Pipeline.Build()
        .Source(new WebSocketAudioSource(ws, new WebSocketAudioOptions { InputSampleRate = openai.InputSampleRate }))
        .Then(OpenAISpeech.StreamingTranscription(openai))
        .Then(new EchoTranscriptionProcessor())
        .Then(OpenAISpeech.Synthesis(openai))
        .Sink(new WebSocketAudioSink(ws));

    await RunAsync(pipeline, ws, ctx, app.Logger);
});

app.Map("/voice/azure-elevenlabs", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    var azure = ReadAzureSpeechOptions(builder.Configuration);
    var elevenlabs = ReadElevenLabsOptions(builder.Configuration);
    if (azure is null) { await Missing(ctx, "AzureSpeech"); return; }
    if (elevenlabs is null) { await Missing(ctx, "ElevenLabs"); return; }

    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    var pipeline = Pipeline.Build()
        .Source(new WebSocketAudioSource(ws, new WebSocketAudioOptions { InputSampleRate = azure.InputSampleRate }))
        .Then(AzureSpeech.StreamingTranscription(azure))
        .Then(new EchoTranscriptionProcessor())
        .Then(ElevenLabs.Synthesis(elevenlabs))
        .Sink(new WebSocketAudioSink(ws));

    await RunAsync(pipeline, ws, ctx, app.Logger);
});

app.Map("/voice/azure-mistral", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    var azure = ReadAzureSpeechOptions(builder.Configuration);
    var mistral = ReadMistralOptions(builder.Configuration);
    if (azure is null) { await Missing(ctx, "AzureSpeech"); return; }
    if (mistral is null) { await Missing(ctx, "Mistral"); return; }

    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    var pipeline = Pipeline.Build()
        .Source(new WebSocketAudioSource(ws, new WebSocketAudioOptions { InputSampleRate = azure.InputSampleRate }))
        .Then(AzureSpeech.StreamingTranscription(azure))
        .Then(new EchoTranscriptionProcessor())
        .Then(Mistral.Synthesis(mistral))
        .Sink(new WebSocketAudioSink(ws));

    await RunAsync(pipeline, ws, ctx, app.Logger);
});

app.Run();

static async Task RunAsync(Pipeline pipeline, System.Net.WebSockets.WebSocket ws, HttpContext ctx, ILogger logger)
{
    await using var runner = new PipelineRunner(pipeline, ctx.RequestAborted);
    try
    {
        await runner.StartAsync(ct: ctx.RequestAborted);
        await runner.WaitAsync().WaitAsync(ctx.RequestAborted);
    }
    catch (OperationCanceledException) { /* client disconnect */ }
    catch (PipelineFailedException ex) { logger.LogError(ex, "Voxa pipeline failed"); }

    if (ws.State == WebSocketState.Open)
    {
        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default); } catch { }
    }
}

static async Task Missing(HttpContext ctx, string section)
{
    ctx.Response.StatusCode = 500;
    await ctx.Response.WriteAsync($"Configuration section '{section}' is missing or incomplete in appsettings.json.");
}

static AzureSpeechOptions? ReadAzureSpeechOptions(IConfiguration cfg)
{
    var key = cfg["AzureSpeech:SubscriptionKey"];
    var region = cfg["AzureSpeech:Region"];
    if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(region)) return null;
    return new AzureSpeechOptions
    {
        SubscriptionKey = key,
        Region = region,
        Voice = cfg["AzureSpeech:Voice"] ?? "en-US-JennyNeural",
        RecognitionLanguage = cfg["AzureSpeech:RecognitionLanguage"] ?? "en-US",
        InputSampleRate = int.TryParse(cfg["AzureSpeech:InputSampleRate"], out var sr) ? sr : 16000,
    };
}

static OpenAISpeechOptions? ReadOpenAIOptions(IConfiguration cfg)
{
    var key = cfg["OpenAI:ApiKey"];
    if (string.IsNullOrEmpty(key)) return null;
    return new OpenAISpeechOptions
    {
        ApiKey = key,
        TtsModel = cfg["OpenAI:TtsModel"] ?? "tts-1",
        TtsVoice = cfg["OpenAI:TtsVoice"] ?? "alloy",
        SttModel = cfg["OpenAI:SttModel"] ?? "whisper-1",
        InputSampleRate = int.TryParse(cfg["OpenAI:InputSampleRate"], out var sr) ? sr : 16000,
    };
}

static ElevenLabsOptions? ReadElevenLabsOptions(IConfiguration cfg)
{
    var key = cfg["ElevenLabs:ApiKey"];
    var voiceId = cfg["ElevenLabs:VoiceId"];
    if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(voiceId)) return null;
    return new ElevenLabsOptions
    {
        ApiKey = key,
        VoiceId = voiceId,
        ModelId = cfg["ElevenLabs:ModelId"] ?? "eleven_multilingual_v2",
    };
}

static MistralSpeechOptions? ReadMistralOptions(IConfiguration cfg)
{
    var key = cfg["Mistral:ApiKey"];
    if (string.IsNullOrEmpty(key)) return null;
    return new MistralSpeechOptions
    {
        ApiKey = key,
        Model = cfg["Mistral:Model"] ?? "voxtral-tts",
        Voice = cfg["Mistral:Voice"] ?? "alloy",
    };
}

/// <summary>
/// Demo-only adapter: forwards each final <see cref="TranscriptionFrame"/> as a
/// <see cref="TextFrame"/> so a downstream TTS can speak it back. Replace with
/// <c>MicrosoftAgentsProcessor</c> for a real LLM-driven granular pipeline.
/// </summary>
internal sealed class EchoTranscriptionProcessor : FrameProcessor
{
    protected override async ValueTask ProcessFrameAsync(Frame frame, CancellationToken ct)
    {
        if (frame is TranscriptionFrame { IsFinal: true } t && !string.IsNullOrWhiteSpace(t.Text))
        {
            await PushFrameAsync(new TextFrame(t.Text), ct);
            return;
        }
        await PushFrameAsync(frame, ct);
    }
}
