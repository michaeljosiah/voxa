using System.Net.WebSockets;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using Voxa.Audio.SileroVad;
using Voxa.Frames;
using Voxa.Pipelines;
using Voxa.Processors;
using Voxa.Services.AzureVoiceLive;
using Voxa.Services.MicrosoftAgents;
using Voxa.Services.OpenAIRealtime;
using Voxa.Speech;
using Voxa.Speech.Azure;
using Voxa.Speech.ElevenLabs;
using Voxa.Speech.Mistral;
using Voxa.Speech.OpenAI;
using Voxa.Transports.WebSocket;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// VAD selector — `Voxa:Vad` config: "Silence" (default, energy-only), "Silero" (ML-based), "None".
// Default is the energy gate because (a) it's the known-good for the demo, (b) Silero's defaults
// take per-environment tuning to match user mic characteristics — opt-in once you've verified
// the rest of the pipeline works.
//
// Tunables (both VADs):
//   Voxa:VadStopMs   — silence required before the gate closes / "end-of-turn" timeout (default 800 ms).
//                       Raise (1200–1500) for slow speakers or anyone who pauses mid-sentence to think.
//                       Lower (400–500) for crisp speakers and snappier turn-taking.
//   Voxa:VadStartMs  — sustained voiced duration before the gate opens (Silero only, default 200 ms).
static FrameProcessor MakeVad(IConfiguration cfg)
{
    var mode = cfg["Voxa:Vad"]?.ToLowerInvariant() ?? "silence";
    var stopMs = int.TryParse(cfg["Voxa:VadStopMs"], out var s) ? s : 800;
    var startMs = int.TryParse(cfg["Voxa:VadStartMs"], out var st) ? st : 200;

    return mode switch
    {
        "silero" or "silerovad" or "ml" => new SileroVadProcessor(new SileroVadOptions
        {
            StartDuration = TimeSpan.FromMilliseconds(startMs),
            StopDuration = TimeSpan.FromMilliseconds(stopMs),
        }),
        "none" or "off" or "disabled" => new PassthroughVad(),
        _ => new SilenceGateProcessor(hangover: TimeSpan.FromMilliseconds(stopMs)),
    };
}


app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(60) });
app.UseDefaultFiles();
app.UseStaticFiles();

app.Map("/voice/voice-live", async (HttpContext ctx) =>
{
    if (!await EnsureWebSocketAsync(ctx)) return;

    var endpoint = builder.Configuration["AzureVoiceLive:Endpoint"];
    var apiKey = builder.Configuration["AzureVoiceLive:ApiKey"];
    if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(apiKey))
    {
        await ReportConfigErrorAsync(ctx, "AzureVoiceLive:Endpoint and AzureVoiceLive:ApiKey must be configured.");
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
    if (!await EnsureWebSocketAsync(ctx)) return;
    var azure = ReadAzureSpeechOptions(builder.Configuration);
    if (azure is null) { await Missing(ctx, "AzureSpeech"); return; }

    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    var pipeline = Pipeline.Build()
        .Source(new WebSocketAudioSource(ws, new WebSocketAudioOptions { InputSampleRate = azure.InputSampleRate }))
        .Then(new AudioArrivalLogger(app.Logger))
        .Then(MakeVad(builder.Configuration))
        .Then(AzureSpeech.StreamingTranscription(azure))
        .Then(new EchoTranscriptionProcessor())
        .Then(AzureSpeech.Synthesis(azure))
        .Sink(new WebSocketAudioSink(ws));

    await RunAsync(pipeline, ws, ctx, app.Logger);
});

app.Map("/voice/openai", async (HttpContext ctx) =>
{
    if (!await EnsureWebSocketAsync(ctx)) return;
    var openai = ReadOpenAIOptions(builder.Configuration);
    if (openai is null) { await Missing(ctx, "OpenAI"); return; }

    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    var pipeline = Pipeline.Build()
        .Source(new WebSocketAudioSource(ws, new WebSocketAudioOptions { InputSampleRate = openai.InputSampleRate }))
        .Then(new AudioArrivalLogger(app.Logger))
        .Then(MakeVad(builder.Configuration))
        .Then(OpenAISpeech.StreamingTranscription(openai))
        .Then(new EchoTranscriptionProcessor())
        .Then(OpenAISpeech.Synthesis(openai))
        .Sink(new WebSocketAudioSink(ws));

    await RunAsync(pipeline, ws, ctx, app.Logger);
});

app.Map("/voice/openai-realtime", async (HttpContext ctx) =>
{
    if (!await EnsureWebSocketAsync(ctx)) return;
    var apiKey = builder.Configuration["OpenAI:ApiKey"];
    if (string.IsNullOrEmpty(apiKey)) { await Missing(ctx, "OpenAI"); return; }

    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    var options = new OpenAIRealtimeOptions
    {
        ApiKey = apiKey,
        Model = builder.Configuration["OpenAI:RealtimeModel"] ?? "gpt-realtime-mini",
        Voice = builder.Configuration["OpenAI:RealtimeVoice"] ?? "alloy",
        Instructions = builder.Configuration["OpenAI:RealtimeInstructions"]
            ?? "You are a friendly voice assistant. Keep responses brief and conversational.",
    };

    var pipeline = Pipeline.Build()
        .Source(new WebSocketAudioSource(ws, new WebSocketAudioOptions { InputSampleRate = options.InputSampleRate }))
        .Then(new OpenAIRealtimeProcessor(options))
        .Sink(new WebSocketAudioSink(ws));

    await RunAsync(pipeline, ws, ctx, app.Logger);
});

// Cost-effective alternative to /voice/openai-realtime: chains Whisper STT + gpt-4o-mini chat +
// OpenAI TTS as separate REST calls. Designed to feel close to Realtime via:
//   - Silero VAD with pre-roll (don't lose first word, don't transcribe breath)
//   - TranscriptionFilter (drop "Thank you" / "Bye" / "." Whisper hallucinations)
//   - SentenceAggregator (TTS speaks the first sentence the moment it lands, doesn't wait for the LLM to finish)
// Cost is roughly $0.40/hr vs Realtime's $18/hr — ~45x cheaper for an extra ~700ms of turn latency.
app.Map("/voice/openai-batch", async (HttpContext ctx) =>
{
    if (!await EnsureWebSocketAsync(ctx)) return;
    var openai = ReadOpenAIOptions(builder.Configuration);
    if (openai is null) { await Missing(ctx, "OpenAI"); return; }

    var chatModel = builder.Configuration["OpenAI:ChatModel"] ?? "gpt-4o-mini";
    var instructions = builder.Configuration["OpenAI:ChatInstructions"]
        ?? "You are a friendly voice assistant. Keep responses to 1-2 short sentences. Be conversational, not formal.";

    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();

    // Build the MAF agent: OpenAI ChatClient -> IChatClient -> ChatClientAgent.
    var openAiClient = new OpenAIClient(openai.ApiKey);
    var chatClient = openAiClient.GetChatClient(chatModel).AsIChatClient();
    var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
    {
        Name = "VoxaSampleAgent",
        ChatOptions = new ChatOptions { Instructions = instructions },
    });

    // Per-connection in-memory session — gives the bot conversation memory for the lifetime of
    // this WebSocket. AgentSession isn't IDisposable, just drop on disconnect.
    // For AONIK integration, replace the no-arg overload with
    //   agent.CreateSessionAsync(conversationId, ct)
    // pointing at the persisted ChatThread id, so memory survives reconnects + flows back to
    // the SSE chat tab.
    var session = await agent.CreateSessionAsync(ctx.RequestAborted);

    var pipeline = Pipeline.Build()
        .Source(new WebSocketAudioSource(ws, new WebSocketAudioOptions { InputSampleRate = openai.InputSampleRate }))
        .Then(new AudioArrivalLogger(app.Logger))
        .Then(MakeVad(builder.Configuration))
        .Then(OpenAISpeech.StreamingTranscription(openai))
        .Then(new TranscriptionFilter())
        .Then(new MicrosoftAgentsProcessor(agent, session))
        .Then(new SentenceAggregator())
        .Then(OpenAISpeech.Synthesis(openai))
        .Sink(new WebSocketAudioSink(ws));

    await RunAsync(pipeline, ws, ctx, app.Logger);
});

app.Map("/voice/azure-elevenlabs", async (HttpContext ctx) =>
{
    if (!await EnsureWebSocketAsync(ctx)) return;
    var azure = ReadAzureSpeechOptions(builder.Configuration);
    var elevenlabs = ReadElevenLabsOptions(builder.Configuration);
    if (azure is null) { await Missing(ctx, "AzureSpeech"); return; }
    if (elevenlabs is null) { await Missing(ctx, "ElevenLabs"); return; }

    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    var pipeline = Pipeline.Build()
        .Source(new WebSocketAudioSource(ws, new WebSocketAudioOptions { InputSampleRate = azure.InputSampleRate }))
        .Then(new AudioArrivalLogger(app.Logger))
        .Then(MakeVad(builder.Configuration))
        .Then(AzureSpeech.StreamingTranscription(azure))
        .Then(new EchoTranscriptionProcessor())
        .Then(ElevenLabs.Synthesis(elevenlabs))
        .Sink(new WebSocketAudioSink(ws));

    await RunAsync(pipeline, ws, ctx, app.Logger);
});

app.Map("/voice/azure-mistral", async (HttpContext ctx) =>
{
    if (!await EnsureWebSocketAsync(ctx)) return;
    var azure = ReadAzureSpeechOptions(builder.Configuration);
    var mistral = ReadMistralOptions(builder.Configuration);
    if (azure is null) { await Missing(ctx, "AzureSpeech"); return; }
    if (mistral is null) { await Missing(ctx, "Mistral"); return; }

    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    var pipeline = Pipeline.Build()
        .Source(new WebSocketAudioSource(ws, new WebSocketAudioOptions { InputSampleRate = azure.InputSampleRate }))
        .Then(new AudioArrivalLogger(app.Logger))
        .Then(MakeVad(builder.Configuration))
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

static async Task<bool> EnsureWebSocketAsync(HttpContext ctx)
{
    if (ctx.WebSockets.IsWebSocketRequest) return true;
    ctx.Response.StatusCode = 400;
    await ctx.Response.WriteAsync("WebSocket required. Open this URL with a WebSocket client (e.g. `new WebSocket(...)` from a browser).");
    return false;
}

static async Task Missing(HttpContext ctx, string section)
    => await ReportConfigErrorAsync(ctx, $"Configuration section '{section}' is missing or incomplete in appsettings.json.");

static async Task ReportConfigErrorAsync(HttpContext ctx, string message)
{
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    var payload = System.Text.Json.JsonSerializer.Serialize(new { type = "error", message });
    var bytes = System.Text.Encoding.UTF8.GetBytes(payload);
    try
    {
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ctx.RequestAborted);
        await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "missing config", default);
    }
    catch { /* client may have already disconnected */ }
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
/// Demo-only adapter: forwards each final <see cref="TranscriptionFrame"/> downstream
/// (so the WebSocket sink emits a <c>transcription</c> envelope and the user bubble appears)
/// AND emits a <see cref="TextFrame"/> with the same text so a downstream TTS can speak it back.
/// Replace with <c>MicrosoftAgentsProcessor</c> for a real LLM-driven granular pipeline.
/// </summary>
internal sealed class EchoTranscriptionProcessor : FrameProcessor
{
    protected override async ValueTask ProcessFrameAsync(Frame frame, CancellationToken ct)
    {
        if (frame is TranscriptionFrame { IsFinal: true } t && !string.IsNullOrWhiteSpace(t.Text))
        {
            await PushFrameAsync(t, ct);                 // surface the transcription to the UI
            await PushFrameAsync(new TextFrame(t.Text), ct); // and have TTS speak it back
            return;
        }
        await PushFrameAsync(frame, ct);
    }
}

/// <summary>No-op processor. Used when <c>Voxa:Vad=None</c> to disable VAD entirely.</summary>
internal sealed class PassthroughVad : FrameProcessor
{
    public PassthroughVad() : base("PassthroughVad") { }
    protected override ValueTask ProcessFrameAsync(Frame frame, CancellationToken ct)
        => PushFrameAsync(frame, ct);
}

/// <summary>
/// Tracks audio-arrival rate and RMS so the server console reveals whether voice is even
/// reaching the pipeline. Insert immediately after WebSocketAudioSource. Logs once per second.
/// </summary>
internal sealed class AudioArrivalLogger : FrameProcessor
{
    private readonly ILogger _logger;
    private int _framesThisSecond;
    private long _bytesThisSecond;
    private double _peakRmsThisSecond;
    private DateTime _windowStart = DateTime.UtcNow;

    public AudioArrivalLogger(ILogger logger) : base("AudioArrivalLogger") { _logger = logger; }

    protected override async ValueTask ProcessFrameAsync(Frame frame, CancellationToken ct)
    {
        if (frame is AudioRawFrame audio)
        {
            _framesThisSecond++;
            _bytesThisSecond += audio.Pcm.Length;
            _peakRmsThisSecond = Math.Max(_peakRmsThisSecond, SilenceGateProcessor.ComputeRms(audio.Pcm.Span));

            var elapsed = DateTime.UtcNow - _windowStart;
            if (elapsed >= TimeSpan.FromSeconds(1))
            {
                _logger.LogInformation(
                    "audio inbound: {Frames} frames / {Bytes} B / peak RMS {Rms:F4} (sample rate {Sr} Hz)",
                    _framesThisSecond, _bytesThisSecond, _peakRmsThisSecond, audio.SampleRate);
                _framesThisSecond = 0;
                _bytesThisSecond = 0;
                _peakRmsThisSecond = 0;
                _windowStart = DateTime.UtcNow;
            }
        }
        await PushFrameAsync(frame, ct).ConfigureAwait(false);
    }
}
