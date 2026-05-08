using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Voxa.Frames;
using Voxa.Processors;
using Voxa.Services.AzureVoiceLive.Events;
using Voxa.Services.AzureVoiceLive.Transport;

namespace Voxa.Services.AzureVoiceLive;

/// <summary>
/// Composite STT+LLM+TTS+VAD processor. Bridges a Voxa <see cref="Frame"/> pipeline to the
/// Azure Voice Live API (and, by configuration, Azure OpenAI Realtime or OpenAI Realtime).
///
/// Inputs accepted: <see cref="AudioRawFrame"/>, <see cref="ToolCallResultFrame"/>.
/// Outputs emitted: <see cref="TranscriptionFrame"/>, <see cref="AudioRawFrame"/>,
/// <see cref="ToolCallRequestFrame"/>, <see cref="BotStartedSpeakingFrame"/>,
/// <see cref="BotStoppedSpeakingFrame"/>, <see cref="UserStartedSpeakingFrame"/>,
/// <see cref="UserStoppedSpeakingFrame"/>, <see cref="InterruptionFrame"/>, and
/// upstream <see cref="ErrorFrame"/>.
/// </summary>
public sealed class AzureVoiceLiveProcessor : FrameProcessor
{
    private readonly AzureVoiceLiveOptions _options;
    private readonly Func<IRealtimeApiTransport> _transportFactory;
    private readonly ILogger<AzureVoiceLiveProcessor> _logger;

    private IRealtimeApiTransport? _transport;
    private Task? _readLoop;
    private volatile bool _botSpeaking;

    /// <param name="options">Session config (endpoint, key, model, voice, tools, …).</param>
    /// <param name="transportFactory">
    /// Optional factory for the wire transport. Defaults to a <see cref="WebSocketRealtimeApiTransport"/>
    /// over <paramref name="options"/>'s endpoint and api key. Override in tests.
    /// </param>
    /// <param name="logger">Optional logger.</param>
    public AzureVoiceLiveProcessor(
        AzureVoiceLiveOptions options,
        Func<IRealtimeApiTransport>? transportFactory = null,
        ILogger<AzureVoiceLiveProcessor>? logger = null)
        : base("AzureVoiceLive", new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        })
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _transportFactory = transportFactory ?? (() => new WebSocketRealtimeApiTransport(options.Endpoint, options.ApiKey));
        _logger = logger ?? NullLogger<AzureVoiceLiveProcessor>.Instance;
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
        if (_transport is null) return;

        switch (frame)
        {
            case AudioRawFrame audio:
                await _transport.SendEventAsync(RealtimeEventCodec.BuildInputAudioBufferAppend(audio.Pcm), ct).ConfigureAwait(false);
                break;

            case ToolCallResultFrame tool:
                await _transport.SendEventAsync(RealtimeEventCodec.BuildToolCallOutput(tool.CallId, tool.ResultJson), ct).ConfigureAwait(false);
                await _transport.SendEventAsync(RealtimeEventCodec.BuildResponseCreate(), ct).ConfigureAwait(false);
                break;
        }
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
                    _logger.LogWarning(ex, "Voice Live: dropped malformed event");
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
            _logger.LogError(ex, "Voice Live read loop failed");
            await PushErrorAsync($"Voice Live read loop failed: {ex.Message}", ex, ct).ConfigureAwait(false);
        }
    }
}
