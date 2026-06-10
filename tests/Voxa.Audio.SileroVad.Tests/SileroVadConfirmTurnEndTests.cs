using Voxa.Audio.SileroVad;
using Voxa.Frames;
using Voxa.Pipelines;
using Voxa.Processors;
using Voxa.Testing.Processors;

namespace Voxa.Audio.SileroVad.Tests;

/// <summary>
/// Covers the smart-turn seam wiring (VPS-001 WS8.1). The confirmer is only consulted at the
/// silence-timeout transition after the gate has opened — it must never fire on pure silence.
/// Full open→pause→confirm behavior depends on real speech audio and is validated where a
/// classifier is wired; here we guard against spurious invocation and verify the null default is
/// inert (the existing processor tests cover the unchanged silence-only path).
/// </summary>
public class SileroVadConfirmTurnEndTests
{
    [Fact]
    public async Task ConfirmTurnEnd_NotInvoked_OnPureSilence()
    {
        int calls = 0;
        var options = new SileroVadOptions
        {
            ConfirmTurnEnd = (_, _) => { Interlocked.Increment(ref calls); return ValueTask.FromResult(true); },
        };

        var captured = new CapturingProcessor();
        var pipeline = Pipeline.Build()
            .Source(new PipelineSource())
            .Then(new SileroVadProcessor(options))
            .Then(captured)
            .Sink(new PipelineSink());

        await using var runner = new PipelineRunner(pipeline);
        await runner.StartAsync();
        await Task.Delay(40);

        // 16 windows of silence — the gate never opens, so no turn-end candidate occurs.
        await pipeline.Source.IngestAsync(new AudioRawFrame(new byte[512 * 16 * 2], 16000, 1));
        await Task.Delay(150);

        Assert.Equal(0, Volatile.Read(ref calls));
        Assert.DoesNotContain(captured.Captured, f => f is UserStoppedSpeakingFrame);
    }
}
