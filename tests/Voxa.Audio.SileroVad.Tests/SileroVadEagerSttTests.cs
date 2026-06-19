using System.Buffers.Binary;
using Voxa.Audio.SileroVad;
using Voxa.Frames;
using Voxa.Pipelines;
using Voxa.Processors;
using Voxa.Testing.Processors;

namespace Voxa.Audio.SileroVad.Tests;

/// <summary>
/// VRT-002 WS1/WS2 state-machine tests driven by a synthetic VAD-probability sequence (the processor's
/// test seam bypasses the ONNX model). "Loud" windows carry energy and are scored voiced; "silent" windows
/// are scored unvoiced — so the gate opens, the hangover accumulates, and eager dispatch / resume-supersede /
/// smart-turn-override / force-split are all exercised deterministically without real speech audio.
/// </summary>
public class SileroVadEagerSttTests
{
    private const int WindowSize = 512;   // 16 kHz
    private const int SampleRate = 16000; // windowDuration = 32 ms

    // Energy probe: any window with real amplitude is "speech" (0.95), silence is "not" (0.02).
    private static float EnergyProbe(float[] window)
    {
        foreach (var s in window)
            if (Math.Abs(s) > 0.05f) return 0.95f;
        return 0.02f;
    }

    private static byte[] Loud(int windows)
    {
        var pcm = new byte[windows * WindowSize * 2];
        for (var i = 0; i < windows * WindowSize; i++)
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), 8000); // ~0.24 normalized ≫ MinRms
        return pcm;
    }

    private static byte[] Silent(int windows) => new byte[windows * WindowSize * 2];

    private static (PipelineRunner Runner, CapturingProcessor Cap, Pipeline Pipeline) Build(SileroVadOptions opts)
    {
        var vad = new SileroVadProcessor(opts, WindowSize, EnergyProbe);
        var cap = new CapturingProcessor();
        var pipeline = Pipeline.Build()
            .Source(new PipelineSource())
            .Then(vad)
            .Then(cap)
            .Sink(new PipelineSink());
        return (new PipelineRunner(pipeline), cap, pipeline);
    }

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task Eager_Dispatch_Emits_Speculative_Marker_Without_Closing_The_Gate()
    {
        var opts = new SileroVadOptions
        {
            StartDuration = TimeSpan.FromMilliseconds(200),
            StopDuration  = TimeSpan.FromMilliseconds(800),
            EagerSttDelay = TimeSpan.FromMilliseconds(300),
        };
        var (runner, cap, pipeline) = Build(opts);

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new AudioRawFrame(Loud(12), SampleRate, 1));   // open the gate
            await pipeline.Source.IngestAsync(new AudioRawFrame(Silent(15), SampleRate, 1)); // 480 ms < stop, > eager

            await cap.WaitForAsync(f => f is SpeculativeUtteranceFrame, Timeout);
            await Task.Delay(50); // settle the remaining silent windows

            var spec = cap.Captured.OfType<SpeculativeUtteranceFrame>().ToList();
            Assert.Single(spec);
            Assert.False(spec[0].Superseded);                       // an arm, not a supersession
            Assert.True(spec[0].UtteranceId > 0);
            Assert.Contains(cap.Captured, f => f is UserStartedSpeakingFrame);
            Assert.DoesNotContain(cap.Captured, f => f is UserStoppedSpeakingFrame); // gate stayed open
        }
    }

    [Fact]
    public async Task Resume_Within_Window_Supersedes_And_Raises_No_Second_UserStarted()
    {
        var opts = new SileroVadOptions
        {
            StartDuration = TimeSpan.FromMilliseconds(200),
            StopDuration  = TimeSpan.FromMilliseconds(800),
            EagerSttDelay = TimeSpan.FromMilliseconds(300),
        };
        var (runner, cap, pipeline) = Build(opts);

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new AudioRawFrame(Loud(12), SampleRate, 1));   // open
            await pipeline.Source.IngestAsync(new AudioRawFrame(Silent(15), SampleRate, 1)); // arm eager
            await pipeline.Source.IngestAsync(new AudioRawFrame(Loud(8), SampleRate, 1));     // resume

            await cap.WaitForAsync(f => f is SpeculativeUtteranceFrame { Superseded: true }, Timeout);
            await Task.Delay(50);

            var spec = cap.Captured.OfType<SpeculativeUtteranceFrame>().ToList();
            Assert.Contains(spec, s => !s.Superseded); // armed
            Assert.Contains(spec, s => s.Superseded);  // discarded on resume
            // Resume is a continuation, not a barge-in: exactly one UserStarted, and the gate never closed.
            Assert.Single(cap.Captured.OfType<UserStartedSpeakingFrame>());
            Assert.DoesNotContain(cap.Captured, f => f is UserStoppedSpeakingFrame);
        }
    }

    [Fact]
    public async Task SmartTurn_False_Supersedes_The_Eager_Pass_And_Keeps_The_Gate_Open()
    {
        var opts = new SileroVadOptions
        {
            StartDuration  = TimeSpan.FromMilliseconds(200),
            StopDuration   = TimeSpan.FromMilliseconds(800),
            EagerSttDelay  = TimeSpan.FromMilliseconds(300),
            ConfirmTurnEnd = (_, _) => ValueTask.FromResult(false), // mid-sentence pause: never confirm
        };
        var (runner, cap, pipeline) = Build(opts);

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new AudioRawFrame(Loud(12), SampleRate, 1));   // open
            await pipeline.Source.IngestAsync(new AudioRawFrame(Silent(30), SampleRate, 1)); // past eager AND stop

            await cap.WaitForAsync(f => f is SpeculativeUtteranceFrame { Superseded: true }, Timeout);
            await Task.Delay(50);

            var spec = cap.Captured.OfType<SpeculativeUtteranceFrame>().ToList();
            Assert.Contains(spec, s => !s.Superseded); // armed at EagerSttDelay
            Assert.Contains(spec, s => s.Superseded);  // smart-turn false > eager ⇒ superseded
            // ConfirmTurnEnd=false keeps the gate open: no end-of-turn emitted.
            Assert.DoesNotContain(cap.Captured, f => f is UserStoppedSpeakingFrame);
        }
    }

    [Fact]
    public async Task EagerSttDelay_Not_Less_Than_StopDuration_Never_Arms()
    {
        var opts = new SileroVadOptions
        {
            StartDuration = TimeSpan.FromMilliseconds(200),
            StopDuration  = TimeSpan.FromMilliseconds(800),
            EagerSttDelay = TimeSpan.FromMilliseconds(800), // == stop ⇒ meaningless, must not fire
        };
        var (runner, cap, pipeline) = Build(opts);

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new AudioRawFrame(Loud(12), SampleRate, 1));
            await pipeline.Source.IngestAsync(new AudioRawFrame(Silent(30), SampleRate, 1)); // past stop ⇒ normal close

            await cap.WaitForAsync(f => f is UserStoppedSpeakingFrame, Timeout);
            await Task.Delay(50);

            Assert.DoesNotContain(cap.Captured, f => f is SpeculativeUtteranceFrame); // never armed
            Assert.Contains(cap.Captured, f => f is UserStoppedSpeakingFrame);         // closed normally
        }
    }

    [Fact]
    public async Task MaxUtteranceDuration_Force_Splits_A_Non_Pausing_Speaker()
    {
        var opts = new SileroVadOptions
        {
            StartDuration        = TimeSpan.FromMilliseconds(200),
            StopDuration         = TimeSpan.FromMilliseconds(800),
            MaxUtteranceDuration = TimeSpan.FromMilliseconds(320), // ~10 windows of open gate
        };
        var (runner, cap, pipeline) = Build(opts);

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new AudioRawFrame(Loud(30), SampleRate, 1)); // continuous speech, no pause

            await cap.WaitForAsync(f => f is UserStoppedSpeakingFrame, Timeout); // a forced split
            await Task.Delay(50);

            // A forced stop mid-stream, then a fresh utterance re-opens — at least two UserStarted edges.
            Assert.Contains(cap.Captured, f => f is UserStoppedSpeakingFrame);
            Assert.True(cap.Captured.OfType<UserStartedSpeakingFrame>().Count() >= 2);
        }
    }
}
