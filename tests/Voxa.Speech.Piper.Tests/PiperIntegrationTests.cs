using System.Diagnostics;
using Voxa.Speech;
using Voxa.Speech.Piper;

namespace Voxa.Speech.Piper.Tests;

/// <summary>
/// Real-binary integration tests (VLS-001 WS2.4). Excluded from the default suite by the
/// LocalModels trait. First local run downloads the pinned piper build (~20 MB) and
/// en_US-amy-low (~60 MB) into the user cache.
/// </summary>
public class PiperIntegrationTests
{
    [Fact]
    [Trait("Category", "LocalModels")]
    public async Task AmyLow_Synthesizes_Real_Audio_And_Leaves_No_Orphans()
    {
        var cache = new VoxaModelCache(
            new VoxaModelCacheOptions(VoxaModelCacheOptions.ResolveCacheRoot(), Offline: false));
        var options = new PiperOptions { Voice = "en_US-amy-low", MaxProcesses = 1 };

        IReadOnlyList<int> spawnedPids;
        try
        {
            await using var engine = new PiperTtsEngine(options, cache);
            await engine.StartAsync(CancellationToken.None);

            // Cold call (includes process spawn + voice load).
            var first = await CollectPcmAsync(engine, "Hello from Voxa.");

            // amy-low is 16 kHz: sanity-check duration is in a sane band for a short sentence.
            var seconds = first.Length / 2.0 / 16000.0;
            Assert.InRange(seconds, 0.5, 10.0);
            Assert.Contains(first, b => b != 0); // actual audio, not digital silence

            // Warm call on the live (pooled) host. Correctness only — wall-clock latency is
            // hardware-dependent and measured by the benchmark harness, not gated here (a hard
            // timing assertion flakes on contended CI runners; VLS-001 §3.1).
            var second = await CollectPcmAsync(engine, "Second sentence, warm process.");
            Assert.NotEmpty(second);

            // Capture this run's live piper pids BEFORE disposal (the engine's DisposeAsync is a
            // no-op for the process-lifetime pool, so they're still alive here).
            spawnedPids = PiperProcessPool.AllLiveProcessIds();
        }
        finally
        {
            PiperProcessPool.DisposeAll();
        }

        // Orphan check: every process this run spawned must be gone. Checked by pid — not a
        // machine-global "piper" name, which races the parallel e2e suite's own piper process.
        Assert.NotEmpty(spawnedPids);
        foreach (var pid in spawnedPids)
            Assert.True(await ExitedWithinAsync(pid, TimeSpan.FromSeconds(10)),
                $"piper process {pid} survived pool disposal");
    }

    private static async Task<bool> ExitedWithinAsync(int pid, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsAlive(pid)) return true;
            await Task.Delay(50);
        }
        return !IsAlive(pid);
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; } // no such process — fully reaped
    }

    [Fact]
    [Trait("Category", "LocalModels")]
    public async Task Relative_VoicePath_Resolves_Against_App_Cwd_Not_The_Child_Working_Dir()
    {
        // Regression: a relative VoicePath must reach piper as a rooted path. The child runs with
        // WorkingDirectory set to a temp output dir, so a relative --model would be resolved there
        // (not the app's CWD where validation found the file) and piper would fail to start —
        // even though startup validation passed.
        var cache = new VoxaModelCache(
            new VoxaModelCacheOptions(VoxaModelCacheOptions.ResolveCacheRoot(), Offline: false));
        Assert.True(PiperVoiceCatalog.TryGet("en_US-amy-low", out var voice));
        var onnxAbs = await cache.ResolveAsync(voice.Onnx, CancellationToken.None);
        var jsonAbs = await cache.ResolveAsync(voice.Json, CancellationToken.None);

        // Stage the voice (+ its required .json sibling) in a directory directly under the current
        // directory so the path is genuinely relative on every platform/volume.
        var stageRel = Path.Combine("voxa-rel-voice-" + Guid.NewGuid().ToString("N"), "amy.onnx");
        var stageAbs = Path.GetFullPath(stageRel);
        Directory.CreateDirectory(Path.GetDirectoryName(stageAbs)!);
        File.Copy(onnxAbs, stageAbs);
        File.Copy(jsonAbs, stageAbs + ".json");
        try
        {
            Assert.False(Path.IsPathRooted(stageRel)); // precondition: the path really is relative

            var options = new PiperOptions { VoicePath = stageRel, OutputSampleRate = 16000, MaxProcesses = 1 };
            await using var engine = new PiperTtsEngine(options, cache);
            await engine.StartAsync(CancellationToken.None);

            var pcm = await CollectPcmAsync(engine, "Relative voice path.");
            Assert.NotEmpty(pcm);            // piper found the voice and synthesized
            Assert.Contains(pcm, b => b != 0);
        }
        finally
        {
            PiperProcessPool.DisposeAll();
            try { Directory.Delete(Path.GetDirectoryName(stageAbs)!, recursive: true); } catch { }
        }
    }

    private static async Task<byte[]> CollectPcmAsync(PiperTtsEngine engine, string text)
    {
        using var ms = new MemoryStream();
        await foreach (var chunk in engine.SynthesizeAsync(text, CancellationToken.None))
            ms.Write(chunk.Span);
        return ms.ToArray();
    }
}
