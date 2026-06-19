using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Voxa.AspNetCore;
using Voxa.Diagnostics;
using Voxa.Frames;
using Voxa.Pipelines;
using Voxa.Testing.Processors;

namespace Voxa.TurnTaking;

/// <summary>
/// Runs ONE corpus sample through the real composed pipeline and reduces the diagnostics hub's events to a
/// <see cref="SampleRecord"/>. It drives <see cref="DefaultVoicePipelineComposer.Compose(IServiceProvider)"/>
/// (the same path a server/Studio uses) with a WAV source head + WAV sink tail, and reads timings from the
/// hub — never a private timer — so the numbers equal what production reports by construction (VRT-001 §4).
/// </summary>
internal static class SampleRunner
{
    public static async Task<SampleRecord> RunAsync(
        IServiceProvider root, CorpusSample sample, string outDir,
        EngineNames engines, TimeSpan wallCap, CancellationToken ct)
    {
        var responseName = $"{sample.Category}__{sample.SampleId}.response.wav";
        var responsePath = Path.Combine(outDir, responseName);
        try
        {
            // One DI scope per sample (a server connection's lifetime): Compose + the hub share it.
            await using var scope = root.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            var composed = sp.GetRequiredService<DefaultVoicePipelineComposer>().Compose(sp);
            var hub = sp.GetRequiredService<VoxaDiagnosticsHub>();

            var builder = Pipeline.Build()
                .Source(new WavFileSourceProcessor(sample.InputWavPath, frameDurationMs: 20));
            foreach (var part in composed.Parts)
                builder = builder.Then(part(sp));
            var pipeline = builder.Sink(new WavFileSinkProcessor(responsePath));

            // Subscribe BEFORE starting — the stage tracker is ordered and only surfaces events published after.
            var events = new List<DiagnosticEvent>();
            using var subCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var pump = Task.Run(async () =>
            {
                try { await foreach (var e in hub.SubscribeAsync(subCts.Token)) events.Add(e); }
                catch (OperationCanceledException) { /* drained */ }
            }, CancellationToken.None);

            var sw = Stopwatch.StartNew();
            try
            {
                // SubscribeAsync only sets HasListeners once its iterator actually starts; wait for that
                // before starting the pipeline so the guarded diagnostics taps don't drop events published
                // before the subscriber registers (which would leave a successful sample with null timings).
                await WaitForListenerAsync(hub, ct).ConfigureAwait(false);

                await using var runner = new PipelineRunner(pipeline, ct);
                await runner.StartAsync(new StartFrame(composed.InputSampleRate, 1), ct).ConfigureAwait(false);
                await runner.WaitAsync().WaitAsync(wallCap, ct).ConfigureAwait(false);

                // Let the subscriber drain the last buffered events before we stop it.
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
            finally
            {
                // Always stop the pump — even when a sample times out / throws — so no SubscribeAsync
                // listener leaks for the rest of the run.
                sw.Stop();
                subCts.Cancel();
                try { await pump.ConfigureAwait(false); } catch { /* best-effort */ }
            }

            var record = new SampleRecord(
                sample.SampleId, sample.Category, engines,
                ReduceTimings(events, sw.Elapsed.TotalMilliseconds),
                new SampleTranscripts(sample.ReferenceText, LastFinalTranscript(events)),
                ReduceSignals(events),
                File.Exists(responsePath) ? responseName : null,
                Error: null);
            WriteRecord(outDir, sample, record);
            return record;
        }
        catch (Exception ex)
        {
            // Failures are data, never a silent drop — write the record with the error + null timings.
            var record = new SampleRecord(
                sample.SampleId, sample.Category, engines,
                new SampleTimings(null, null, null, null, null),
                new SampleTranscripts(sample.ReferenceText, null),
                new TurnSignals(0, 0),
                File.Exists(responsePath) ? responseName : null,
                Error: $"{ex.GetType().Name}: {ex.Message}");
            WriteRecord(outDir, sample, record);
            return record;
        }
    }

    private static SampleTimings ReduceTimings(IReadOnlyList<DiagnosticEvent> events, double totalWallMs)
    {
        double? Stage(string name) => events.OfType<StageLatencyEvent>().FirstOrDefault(e => e.Stage == name)?.Ms;

        // Voice-to-voice: the UserStopped turn edge → the first synthesized audio chunk (== voxa.turn.ttfb).
        double? ttfb = null;
        var userStopped = events.OfType<TurnEvent>().FirstOrDefault(e => e.Edge == TurnEdge.UserStopped);
        var firstTts = events.OfType<TtsChunkEvent>().FirstOrDefault();
        if (userStopped is not null && firstTts is not null)
            ttfb = (firstTts.TimestampMicros - userStopped.TimestampMicros) / 1000.0;

        return new SampleTimings(
            Stage("stt_final"), Stage("agent_first_token"), Stage("tts_first_byte"), ttfb, totalWallMs);
    }

    private static async Task WaitForListenerAsync(VoxaDiagnosticsHub hub, CancellationToken ct)
    {
        // Bounded spin (~200 ms ceiling) — the Task.Run subscriber registers near-instantly in practice.
        for (var i = 0; i < 200 && !hub.HasListeners; i++)
            await Task.Delay(1, ct).ConfigureAwait(false);
    }

    private static TurnSignals ReduceSignals(IReadOnlyList<DiagnosticEvent> events)
    {
        var turns = events.OfType<TurnEvent>().ToList();
        return new TurnSignals(
            UserStoppedEdges: turns.Count(e => e.Edge == TurnEdge.UserStopped),
            BotStartedEdges: turns.Count(e => e.Edge == TurnEdge.BotStarted));
    }

    private static string? LastFinalTranscript(IReadOnlyList<DiagnosticEvent> events)
        => events.OfType<TranscriptEvent>().LastOrDefault(e => e.IsFinal)?.Text;

    private static void WriteRecord(string outDir, CorpusSample sample, SampleRecord record)
        => File.WriteAllText(
            Path.Combine(outDir, $"{sample.Category}__{sample.SampleId}.json"),
            JsonSerializer.Serialize(record, JsonOpts));

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
}
