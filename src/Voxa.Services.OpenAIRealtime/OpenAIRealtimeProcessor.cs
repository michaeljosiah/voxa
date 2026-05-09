using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Voxa.Frames;
using Voxa.Processors;
using Voxa.Services.OpenAIRealtime.Events;
using Voxa.Services.OpenAIRealtime.Transport;

namespace Voxa.Services.OpenAIRealtime;

/// <summary>
/// Composite STT+LLM+TTS+VAD processor for the OpenAI Realtime API. Bridges a Voxa
/// <see cref="Frame"/> pipeline to a full-duplex streaming voice session — server-side VAD,
/// native interruption, sub-400ms turns. The C# equivalent of Pipecat's
/// <c>OpenAIRealtimeBetaService</c>.
///
/// <para>
/// Inputs accepted: <see cref="AudioRawFrame"/>, <see cref="ToolCallResultFrame"/>.
/// Outputs emitted: <see cref="TranscriptionFrame"/>, <see cref="AudioRawFrame"/>,
/// <see cref="LlmTextChunkFrame"/>, <see cref="ToolCallRequestFrame"/>,
/// <see cref="BotStartedSpeakingFrame"/>, <see cref="BotStoppedSpeakingFrame"/>,
/// <see cref="UserStartedSpeakingFrame"/>, <see cref="UserStoppedSpeakingFrame"/>,
/// <see cref="InterruptionFrame"/>, and upstream <see cref="ErrorFrame"/>.
/// </para>
/// </summary>
public sealed class OpenAIRealtimeProcessor : FrameProcessor
{
    private readonly OpenAIRealtimeOptions _options;
    private readonly Func<IRealtimeApiTransport> _transportFactory;
    private readonly ILogger<OpenAIRealtimeProcessor> _logger;

    private IRealtimeApiTransport? _transport;
    private Task? _readLoop;
    private volatile bool _botSpeaking;

    /// <param name="options">Session config (api key, model, voice, tools, …).</param>
    /// <param name="transportFactory">
    /// Optional factory for the wire transport. Defaults to a <see cref="WebSocketRealtimeApiTransport"/>
    /// over <paramref name="options"/>'s endpoint, api key, and model. Override in tests.
    /// </param>
    /// <param name="logger">Optional logger.</param>
    public OpenAIRealtimeProcessor(
        OpenAIRealtimeOptions options,
        Func<IRealtimeApiTransport>? transportFactory = null,
        ILogger<OpenAIRealtimeProcessor>? logger = null)
        : base("OpenAIRealtime", new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        })
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _transportFactory = transportFactory
            ?? (() => new WebSocketRealtimeApiTransport(options.Endpoint, options.ApiKey, options.Model));
        _logger = logger ?? NullLogger<OpenAIRealtimeProcessor>.Instance;
    }

    protected override async ValueTask OnStartAsync(StartFrame frame, CancellationToken ct)
    {
        _transport = _transportFactory();
        await _transport.ConnectAsync(ct).ConfigureAwait(false);
        await _transport.SendEventAsync(RealtimeEventCodec.BuildSessionUpdate(_options), ct).ConfigureAwait(false);
        _readLoop = Task.Run(() => ReadEventsLoopAsync(ct));
    }

    protected override async ValueTask OnEndAsync(EndFrame frame, CancellationToken ct)
    {
        if (_transport is not null)
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
            _transport = null;
        }

        if (_readLoop is not null)
        {
            try { await _readLoop.ConfigureAwait(false); } catch { }
            _readLoop = null;
        }
    }

    protected override async ValueTask ProcessFrameAsync(Frame frame, CancellationToken ct)
    {
        if (_transport is not null)
        {
            switch (frame)
            {
                case AudioRawFrame audio:
                    // Audio is consumed by the Realtime API; assistant audio comes back via the read loop.
                    await _transport.SendEventAsync(RealtimeEventCodec.BuildInputAudioBufferAppend(audio.Pcm), ct).ConfigureAwait(false);
                    return;

                case ToolCallResultFrame tool:
                    await _transport.SendEventAsync(RealtimeEventCodec.BuildToolCallOutput(tool.CallId, tool.ResultJson), ct).ConfigureAwait(false);
                    await _transport.SendEventAsync(RealtimeEventCodec.BuildResponseCreate(), ct).ConfigureAwait(false);
                    return;
            }
        }

        // Forward everything else (StartFrame, EndFrame, …) downstream so the sink can complete.
        await PushFrameAsync(frame, ct).ConfigureAwait(false);
    }

    private async Task ReadEventsLoopAsync(CancellationToken ct)
    {
        var transport = _transport;
        if (transport is null) return;

        try
        {
            await foreach (var json in transport.ReadEventsAsync(ct).ConfigureAwait(false))
            {
                JsonElement root;
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    root = doc.RootElement.Clone();
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "OpenAI Realtime: dropped malformed event");
                    continue;
                }

                foreach (var emitted in RealtimeEventCodec.Decode(root, _botSpeaking, _options.OutputSampleRate))
                {
                    if (emitted is BotStartedSpeakingFrame) _botSpeaking = true;
                    else if (emitted is BotStoppedSpeakingFrame) _botSpeaking = false;

                    await PushFrameAsync(emitted, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenAI Realtime read loop failed");
            await PushErrorAsync($"OpenAI Realtime read loop failed: {ex.Message}", ex, ct).ConfigureAwait(false);
        }
    }
}
