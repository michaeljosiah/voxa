using System.Diagnostics;
using Voxa.Frames;
using Voxa.Processors;

namespace Voxa.Observability;

/// <summary>
/// Drop-in pass-through processor that emits an <see cref="Activity"/> on
/// <see cref="VoxaActivities.Source"/> for every frame it observes. Subscribe via
/// OpenTelemetry to capture per-frame spans:
///
/// <code>
/// services.AddOpenTelemetry()
///     .WithTracing(t => t.AddSource(VoxaActivities.SourceName).AddOtlpExporter());
/// </code>
///
/// Insert wherever you want a probe — typically right after the source and right before the sink:
/// <code>
/// Pipeline.Build()
///     .Source(...)
///     .Then(new TracingProcessor("user-input"))
///     .Then(new AzureVoiceLiveProcessor(opts))
///     .Then(new TracingProcessor("voice-live-out"))
///     .Sink(...);
/// </code>
/// </summary>
public sealed class TracingProcessor : FrameProcessor
{
    private readonly string _scope;

    public TracingProcessor(string scope = "frame") : base($"Tracing[{scope}]")
    {
        _scope = scope;
    }

    protected override async ValueTask ProcessFrameAsync(Frame frame, CancellationToken ct)
    {
        using var activity = VoxaActivities.Source.StartActivity($"voxa.{_scope}.{frame.GetType().Name}");
        if (activity is not null)
        {
            activity.SetTag("voxa.scope", _scope);
            activity.SetTag("voxa.frame.id", frame.Id);
            activity.SetTag("voxa.frame.type", frame.GetType().Name);
            activity.SetTag("voxa.frame.direction", frame.Direction.ToString());

            switch (frame)
            {
                case AudioRawFrame a:
                    activity.SetTag("voxa.audio.sample_rate", a.SampleRate);
                    activity.SetTag("voxa.audio.channels", a.Channels);
                    activity.SetTag("voxa.audio.bytes", a.Pcm.Length);
                    break;
                case TranscriptionFrame t:
                    activity.SetTag("voxa.transcription.is_final", t.IsFinal);
                    activity.SetTag("voxa.transcription.text_length", t.Text.Length);
                    break;
                case ToolCallRequestFrame call:
                    activity.SetTag("voxa.tool.name", call.Name);
                    activity.SetTag("voxa.tool.call_id", call.CallId);
                    break;
                case ErrorFrame err:
                    activity.SetStatus(ActivityStatusCode.Error, err.Message);
                    break;
            }
        }

        await PushFrameAsync(frame, ct).ConfigureAwait(false);
    }
}
