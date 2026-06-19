using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Voxa.AspNetCore;
using Voxa.Diagnostics;
using Voxa.Frames;
using Voxa.Pipelines;
using Voxa.Processors;
using Voxa.Studio.Audio;

namespace Voxa.Studio.Services;

/// <summary>
/// One live Talk conversation (VST-001 WS1): Studio's equivalent of a server's WebSocket
/// connection. Creates a DI scope, composes the SAME default pipeline a server runs
/// (<see cref="DefaultVoicePipelineComposer.Compose(IServiceProvider)"/>), and bridges it to
/// the local audio devices — mic frames in at the source, synthesized audio out at the sink.
///
/// <para>
/// Barge-in mirrors <c>WebSocketAudioSink</c>'s epoch behavior: a <c>UserStartedSpeakingFrame</c>
/// or <c>InterruptionFrame</c> reaching the sink flushes the device's queued playback, and the
/// flush instant is published as the <c>audio_out</c>-adjacent signal renderers care about.
/// </para>
/// </summary>
public sealed class TalkSession : IAsyncDisposable
{
    private readonly IServiceScope _scope;
    private readonly IStudioAudioDevice _device;
    private readonly Pipeline _pipeline;
    private readonly PipelineRunner _runner;
    private readonly CancellationTokenSource _cts = new();
    private Task? _micPump;
    private Task? _speakerPump;
    private bool _started;
    private readonly MicGate _micGate;

    public VoxaDiagnosticsHub Hub { get; }
    public int InputSampleRate { get; }
    public int OutputSampleRate { get; }

    private TalkSession(
        IServiceScope scope, IStudioAudioDevice device, ComposedVoice composed, bool allowBargeIn)
    {
        _scope = scope;
        _device = device;
        _micGate = new MicGate(allowBargeIn);
        Hub = scope.ServiceProvider.GetRequiredService<VoxaDiagnosticsHub>();
        InputSampleRate = composed.InputSampleRate;
        OutputSampleRate = composed.OutputSampleRate;

        var builder = Pipeline.Build().Source(new PipelineSource("StudioMic"));
        foreach (var part in composed.Parts)
            builder.Then(part(scope.ServiceProvider));
        _pipeline = builder.Sink(new PipelineSink("StudioSpeaker"));
        _runner = new PipelineRunner(_pipeline, _cts.Token);
    }

    /// <summary>Compose a session from the app's root provider. Throws the composer/validator errors verbatim.</summary>
    public static TalkSession Create(IServiceProvider root, IStudioAudioDevice device)
        => Create(root, device,
            sp => sp.GetRequiredService<DefaultVoicePipelineComposer>().Compose(sp));

    /// <summary>
    /// Compose with a caller-supplied composition (VST-002 D3): the Builder canvas runs its
    /// compiled chain through the same session — scope, pumps, barge-in — Talk uses.
    /// </summary>
    public static TalkSession Create(
        IServiceProvider root, IStudioAudioDevice device, Func<IServiceProvider, ComposedVoice> compose)
    {
        var scope = root.CreateScope();
        try
        {
            return new TalkSession(scope, device, compose(scope.ServiceProvider), ReadAllowBargeIn(root));
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    // Half-duplex by default (Voxa:Studio:AllowBargeIn=false): on speakers the mic must not feed the
    // bot's own audio back into the pipeline. Set it true for full-duplex barge-in (use headphones).
    private static bool ReadAllowBargeIn(IServiceProvider services)
        => bool.TryParse(
            (services.GetService(typeof(IConfiguration)) as IConfiguration)?["Voxa:Studio:AllowBargeIn"],
            out var allow) && allow;

    // Playback duration of a queued PCM16 frame (bytes ÷ bytes-per-second) — how long the bot stays
    // audible, which is what the mic gate needs since RenderAsync only enqueues.
    private static TimeSpan PcmDuration(AudioRawFrame audio)
    {
        var bytesPerSecond = audio.SampleRate * audio.Channels * 2; // 16-bit samples
        return bytesPerSecond > 0
            ? TimeSpan.FromSeconds(audio.Pcm.Length / (double)bytesPerSecond)
            : TimeSpan.Zero;
    }

    /// <summary>[source, parts…, sink] — index i+1 is composed part i. Queue-depth badges read this.</summary>
    internal IReadOnlyList<FrameProcessor> Processors => _pipeline.Processors;

    /// <summary>Start the pipeline and both audio pumps.</summary>
    public async Task StartAsync(AudioEndpoint microphone, AudioEndpoint speaker)
    {
        if (_started) throw new InvalidOperationException("TalkSession is already started.");
        _started = true;

        await _device.StartRenderAsync(speaker, OutputSampleRate, _cts.Token).ConfigureAwait(false);
        await _runner.StartAsync(new StartFrame(InputSampleRate, Channels: 1), _cts.Token).ConfigureAwait(false);

        _micPump = Task.Run(() => PumpMicAsync(microphone, _cts.Token));
        _speakerPump = Task.Run(() => PumpSpeakerAsync(_cts.Token));
    }

    /// <summary>Completes when the pipeline ends or fails — surface this to the UI.</summary>
    public Task WaitAsync() => _runner.WaitAsync();

    private async Task PumpMicAsync(AudioEndpoint microphone, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _device.CaptureAsync(microphone, InputSampleRate, ct).ConfigureAwait(false))
            {
                // Half-duplex echo suppression (P1): drop captured audio while the bot is speaking, so an
                // on-speaker user doesn't loop the bot's own output back through VAD → STT → agent.
                if (!_micGate.ShouldIngest()) continue;
                await _pipeline.Source.IngestAsync(new AudioRawFrame(frame, InputSampleRate, 1), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* session stop */ }
    }

    private async Task PumpSpeakerAsync(CancellationToken ct)
    {
        // First audio frame of each bot turn contributes the waterfall's final segment:
        // pipeline sink → device queue (in-process this is the channel hop + enqueue cost).
        bool awaitFirstAudio = false;
        try
        {
            await foreach (var frame in _pipeline.Sink.ReadAllAsync(ct).ConfigureAwait(false))
            {
                switch (frame)
                {
                    case BotStartedSpeakingFrame:
                        awaitFirstAudio = true;
                        _micGate.BotStartedSpeaking(); // close the mic gate while the bot talks
                        break;

                    case BotStoppedSpeakingFrame:
                        // Priority-routed: arrives while queued audio may still be playing. Only clears
                        // the flag — the playback tail (NoteRenderedAudio) governs when the mic reopens.
                        _micGate.BotStoppedSpeaking();
                        break;

                    case AudioRawFrame audio:
                        var t0 = awaitFirstAudio && Hub.HasListeners ? Hub.NowMicros() : 0;
                        await _device.RenderAsync(audio.Pcm, ct).ConfigureAwait(false);
                        // RenderAsync only queues; tell the gate how long this audio will actually be
                        // audible so the mic stays shut until the speakers drain (echo suppression).
                        _micGate.NoteRenderedAudio(PcmDuration(audio));
                        if (awaitFirstAudio)
                        {
                            awaitFirstAudio = false;
                            if (Hub.HasListeners)
                                Hub.Publish(new StageLatencyEvent("audio_out", (Hub.NowMicros() - t0) / 1000.0));
                        }
                        break;

                    // Barge-in (the WebSocketAudioSink epoch rule, device edition): stale queued
                    // playback is cut the moment the user speaks over the bot.
                    case UserStartedSpeakingFrame:
                    case InterruptionFrame:
                        await _device.FlushRenderAsync().ConfigureAwait(false);
                        _micGate.PlaybackFlushed(); // dropped audio won't play — let the gate reopen promptly
                        break;
                }
            }
        }
        catch (OperationCanceledException) { /* session stop */ }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _runner.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        await _runner.DisposeAsync().ConfigureAwait(false);
        try { if (_micPump is not null) await _micPump.ConfigureAwait(false); } catch { }
        try { if (_speakerPump is not null) await _speakerPump.ConfigureAwait(false); } catch { }
        await _device.FlushRenderAsync().ConfigureAwait(false);
        _scope.Dispose();
        _cts.Dispose();
    }
}
