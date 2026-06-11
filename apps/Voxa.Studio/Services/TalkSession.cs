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

    public VoxaDiagnosticsHub Hub { get; }
    public int InputSampleRate { get; }
    public int OutputSampleRate { get; }

    private TalkSession(
        IServiceScope scope, IStudioAudioDevice device, ComposedVoice composed)
    {
        _scope = scope;
        _device = device;
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
    {
        var scope = root.CreateScope();
        try
        {
            var composer = scope.ServiceProvider.GetRequiredService<DefaultVoicePipelineComposer>();
            return new TalkSession(scope, device, composer.Compose(scope.ServiceProvider));
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

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
                await _pipeline.Source.IngestAsync(new AudioRawFrame(frame, InputSampleRate, 1), ct).ConfigureAwait(false);
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
                        break;

                    case AudioRawFrame audio:
                        var t0 = awaitFirstAudio && Hub.HasListeners ? Hub.NowMicros() : 0;
                        await _device.RenderAsync(audio.Pcm, ct).ConfigureAwait(false);
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
