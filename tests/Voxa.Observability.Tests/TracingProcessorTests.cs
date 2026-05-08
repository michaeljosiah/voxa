using System.Diagnostics;
using Voxa;
using Voxa.Frames;
using Voxa.Pipelines;
using Voxa.Processors;
using Voxa.Testing.Processors;

namespace Voxa.Observability.Tests;

public class TracingProcessorTests
{
    private static (List<Activity> Activities, ActivityListener Listener) CaptureActivities()
    {
        var activities = new List<Activity>();
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == VoxaActivities.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => { lock (activities) activities.Add(a); },
        };
        ActivitySource.AddActivityListener(listener);
        return (activities, listener);
    }

    [Fact]
    public async Task Emits_Activity_Per_Frame_With_Type_Tag()
    {
        var (activities, listener) = CaptureActivities();
        using (listener)
        {
            var pipeline = Pipeline.Build()
                .Source(new PipelineSource())
                .Then(new TracingProcessor("test"))
                .Then(new CapturingProcessor())
                .Sink(new PipelineSink());

            await using var runner = new PipelineRunner(pipeline);
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new TextFrame("hello"));
            await Task.Delay(80);

            List<Activity> snapshot;
            lock (activities) snapshot = activities.ToList();

            Assert.NotEmpty(snapshot);
            Assert.Contains(snapshot, a =>
                a.OperationName.Contains("TextFrame") &&
                a.GetTagItem("voxa.frame.type") as string == "TextFrame" &&
                a.GetTagItem("voxa.scope") as string == "test");
        }
    }

    [Fact]
    public async Task Emits_Audio_Tags_For_AudioRawFrame()
    {
        var (activities, listener) = CaptureActivities();
        using (listener)
        {
            var pipeline = Pipeline.Build()
                .Source(new PipelineSource())
                .Then(new TracingProcessor("audio"))
                .Sink(new PipelineSink());

            await using var runner = new PipelineRunner(pipeline);
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new AudioRawFrame(new byte[] { 1, 2, 3, 4 }, 24000, 1));
            await Task.Delay(80);

            List<Activity> snapshot;
            lock (activities) snapshot = activities.ToList();

            var audioActivity = snapshot.FirstOrDefault(a => a.GetTagItem("voxa.frame.type") as string == "AudioRawFrame");
            Assert.NotNull(audioActivity);
            Assert.Equal(24000, audioActivity!.GetTagItem("voxa.audio.sample_rate"));
            Assert.Equal(1, audioActivity.GetTagItem("voxa.audio.channels"));
            Assert.Equal(4, audioActivity.GetTagItem("voxa.audio.bytes"));
        }
    }

    [Fact]
    public async Task Sets_Error_Status_On_ErrorFrame()
    {
        var (activities, listener) = CaptureActivities();
        using (listener)
        {
            var pipeline = Pipeline.Build()
                .Source(new PipelineSource())
                .Then(new TracingProcessor("err"))
                .Sink(new PipelineSink());

            await using var runner = new PipelineRunner(pipeline);
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new ErrorFrame("boom"));
            await Task.Delay(80);

            List<Activity> snapshot;
            lock (activities) snapshot = activities.ToList();

            var errorActivity = snapshot.FirstOrDefault(a => a.GetTagItem("voxa.frame.type") as string == "ErrorFrame");
            Assert.NotNull(errorActivity);
            Assert.Equal(ActivityStatusCode.Error, errorActivity!.Status);
            Assert.Equal("boom", errorActivity.StatusDescription);
        }
    }

    [Fact]
    public async Task Forwards_Frames_Unchanged()
    {
        var captured = new CapturingProcessor();
        var pipeline = Pipeline.Build()
            .Source(new PipelineSource())
            .Then(new TracingProcessor("tap"))
            .Then(captured)
            .Sink(new PipelineSink());

        await using var runner = new PipelineRunner(pipeline);
        await runner.StartAsync();
        await pipeline.Source.IngestAsync(new TextFrame("hello"));
        await captured.WaitForAsync(2, TimeSpan.FromSeconds(2));

        Assert.Contains(captured.Captured, f => f is TextFrame t && t.Text == "hello");
    }
}
