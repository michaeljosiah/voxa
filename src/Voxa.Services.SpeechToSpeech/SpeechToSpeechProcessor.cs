using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Voxa.Frames;
using Voxa.Processors;
using Voxa.Speech;

namespace Voxa.Services.SpeechToSpeech;

/// <summary>
/// Full-duplex speech-to-speech composite (VRT-005 WS2), built to the same blueprint as
/// <c>OpenAIRealtimeProcessor</c>: a bounded <see cref="BoundedChannelFullMode.DropOldest"/> data channel, a
/// session created on <see cref="OnStartAsync"/>, a read loop draining the session's
/// <see cref="ISpeechToSpeechSession.RespondAsync"/> stream and pushing frames, and graceful teardown on
/// <see cref="OnEndAsync"/>. The only substantive difference from the cloud composites is that the wire transport
/// is replaced by an in-process (or sidecar) <see cref="ISpeechToSpeechSession"/>.
///
/// <para>
/// Inputs accepted: <see cref="AudioRawFrame"/> (user audio in), <see cref="InterruptionFrame"/> (barge-in →
/// <see cref="ISpeechToSpeechSession.CancelAsync"/>). Outputs emitted: <see cref="AudioRawFrame"/> (agent audio),
/// <see cref="LlmTextChunkFrame"/>, <see cref="BotStartedSpeakingFrame"/> / <see cref="BotStoppedSpeakingFrame"/>,
/// <see cref="UserStartedSpeakingFrame"/> / <see cref="UserStoppedSpeakingFrame"/>, <see cref="InterruptionFrame"/>,
/// and upstream <see cref="ErrorFrame"/> — the same vocabulary the cloud composites emit, so Studio / the
/// diagnostics hub / a sink cannot tell a third-party realtime API from a local S2S model.
/// </para>
/// </summary>
public sealed class SpeechToSpeechProcessor : FrameProcessor
{
    private readonly Func<ISpeechToSpeechSession> _sessionFactory;
    private readonly SpeechToSpeechOptions _options;
    private readonly ILogger<SpeechToSpeechProcessor> _logger;

    private ISpeechToSpeechSession? _session;
    private Task? _readLoop;
    private volatile bool _botSpeaking;

    /// <param name="sessionFactory">Builds the full-duplex session (created once per <see cref="OnStartAsync"/>).</param>
    /// <param name="options">Voice / system-prompt applied on start.</param>
    /// <param name="logger">Optional logger.</param>
    public SpeechToSpeechProcessor(
        Func<ISpeechToSpeechSession> sessionFactory,
        SpeechToSpeechOptions options,
        ILogger<SpeechToSpeechProcessor>? logger = null)
        : base("SpeechToSpeech", new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        })
    {
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<SpeechToSpeechProcessor>.Instance;
    }

    protected override async ValueTask OnStartAsync(StartFrame frame, CancellationToken ct)
    {
        _session = _sessionFactory()
            ?? throw new InvalidOperationException("SpeechToSpeech session factory returned null.");

        await _session.SetVoiceAsync(_options.Voice, ct).ConfigureAwait(false);
        if (_options.SystemPrompt is { } systemPrompt)
            await _session.SetSystemPromptAsync(systemPrompt, ct).ConfigureAwait(false);

        // Started on the processor-lifetime token (ct) so it survives interruptions, exactly like the cloud
        // composites' read loops.
        _readLoop = Task.Run(() => ReadResponseLoopAsync(ct), ct);
    }

    protected override async ValueTask ProcessFrameAsync(Frame frame, CancellationToken ct)
    {
        if (_session is not null && frame is AudioRawFrame audio)
        {
            // User audio is consumed by the model's full-duplex loop; agent audio returns via the read loop.
            await _session.AppendUserAudioAsync(audio.Pcm, ct).ConfigureAwait(false);
            return;
        }

        // Forward everything else (StartFrame, EndFrame, InterruptionFrame, …) downstream so the sink completes
        // and downstream observers see the same frames the cloud composites forward.
        await PushFrameAsync(frame, ct).ConfigureAwait(false);
    }

    protected override async ValueTask OnInterruptionAsync(InterruptionFrame frame, CancellationToken ct)
    {
        // Barge-in reached us from upstream: abort the model's in-flight response and reset the speaking edge
        // so the next turn re-announces BotStarted. The base then forwards the InterruptionFrame downstream via
        // ProcessFrameAsync, so the sink purges queued bot audio.
        _botSpeaking = false;
        if (_session is not null)
            await _session.CancelAsync(ct).ConfigureAwait(false);
    }

    private async Task ReadResponseLoopAsync(CancellationToken ct)
    {
        var session = _session;
        if (session is null) return;

        try
        {
            await foreach (var chunk in session.RespondAsync(ct).ConfigureAwait(false))
            {
                // Session events (the model's own VAD / barge-in) map to the same speaking-edge frames the
                // cloud composites emit.
                switch (chunk.Event)
                {
                    case SpeechToSpeechEvent.UserStartedSpeaking:
                        await PushFrameAsync(new UserStartedSpeakingFrame(), ct).ConfigureAwait(false);
                        break;
                    case SpeechToSpeechEvent.UserStoppedSpeaking:
                        await PushFrameAsync(new UserStoppedSpeakingFrame(), ct).ConfigureAwait(false);
                        break;
                    case SpeechToSpeechEvent.Interrupted:
                        // The model aborted its turn; the InterruptionFrame tells downstream to purge bot audio,
                        // and resets the speaking edge so the next turn re-announces BotStarted.
                        _botSpeaking = false;
                        await PushFrameAsync(new InterruptionFrame(), ct).ConfigureAwait(false);
                        break;
                }

                if (!chunk.AudioPcm.IsEmpty && !_botSpeaking)
                {
                    _botSpeaking = true;
                    await PushFrameAsync(new BotStartedSpeakingFrame(), ct).ConfigureAwait(false);
                }
                if (chunk.Text is { Length: > 0 } text)
                    await PushFrameAsync(new LlmTextChunkFrame(text), ct).ConfigureAwait(false);
                if (!chunk.AudioPcm.IsEmpty)
                    await PushFrameAsync(new AudioRawFrame(chunk.AudioPcm, session.OutputSampleRate, 1), ct).ConfigureAwait(false);
                if (chunk.IsFinal && _botSpeaking)
                {
                    _botSpeaking = false;
                    await PushFrameAsync(new BotStoppedSpeakingFrame(), ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SpeechToSpeech read loop failed");
            await PushErrorAsync($"SpeechToSpeech read loop failed: {ex.Message}", ex, ct).ConfigureAwait(false);
        }
    }

    protected override async ValueTask OnEndAsync(EndFrame frame, CancellationToken ct)
    {
        if (_session is not null)
        {
            await _session.DisposeAsync().ConfigureAwait(false);
            _session = null;
        }

        if (_readLoop is not null)
        {
            try { await _readLoop.ConfigureAwait(false); }
            catch { /* swallow on teardown — the session was disposed out from under the loop */ }
            _readLoop = null;
        }
    }
}
