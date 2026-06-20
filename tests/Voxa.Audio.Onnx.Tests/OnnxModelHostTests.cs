using Voxa.Speech;

namespace Voxa.Audio.Onnx.Tests;

/// <summary>
/// Host tests touch the process-wide session cache, so each one brackets itself with
/// <see cref="OnnxModelHost.EvictAll"/>. They all live in this one class, which xUnit runs sequentially.
/// </summary>
public sealed class OnnxModelHostTests : IDisposable
{
    private static string ModelPath => Path.Combine(AppContext.BaseDirectory, "silero_vad.onnx");

    public OnnxModelHostTests() => OnnxModelHost.EvictAll();
    public void Dispose() => OnnxModelHost.EvictAll();

    [Fact]
    public void Load_returns_a_session_with_metadata_on_cpu()
    {
        var host = new OnnxModelHost();
        var session = host.Load(ModelPath);

        Assert.NotNull(session.Session);
        Assert.Equal(OnnxDevice.Cpu, session.ActiveDevice);
        // Silero v5's ONNX contract: inputs (input, state, sr), outputs (output, stateN).
        Assert.Contains("input", session.InputNames);
        Assert.Contains("sr", session.InputNames);
        Assert.NotEmpty(session.OutputNames);
    }

    [Fact]
    public void Load_caches_one_instance_per_path_and_device()
    {
        var host = new OnnxModelHost();
        var a = host.Load(ModelPath);
        var b = host.Load(ModelPath);
        Assert.Same(a, b); // same (path, device) → one shared instance

        // An odd-but-equivalent path form normalises to the same cache key.
        var c = host.Load(Path.Combine(AppContext.BaseDirectory, ".", "silero_vad.onnx"));
        Assert.Same(a, c);
    }

    [Fact]
    public void EvictAll_drops_cached_sessions_so_the_next_load_recreates()
    {
        var host = new OnnxModelHost();
        var first = host.Load(ModelPath);
        OnnxModelHost.EvictAll();
        var second = host.Load(ModelPath);
        Assert.NotSame(first, second);

        // EvictAll also disposes each session (not just drops it) — see OnnxModelHost.EvictAll. We assert
        // the cache-drop here and leave disposal to inspection on purpose: ORT has no safe managed probe for
        // a disposed session — InputMetadata is cached (no throw) and Run aborts the process (native AV)
        // rather than throwing, so any "is it disposed?" assertion would be unreliable or host-crashing.
        var reloaded = host.Load(ModelPath);
        Assert.Same(second, reloaded); // the post-evict session is itself cached normally
    }

    [Fact]
    public void Load_runs_the_hook_after_the_device_and_before_the_build()
    {
        var host = new OnnxModelHost();
        var hookRan = false;
        string? hookPath = null;
        var hookDevice = (OnnxDevice?)null;

        host.Load(ModelPath, OnnxDevice.Cpu, (opts, path, device) =>
        {
            hookRan = true;
            hookPath = path;
            hookDevice = device;
        });

        Assert.True(hookRan);
        Assert.Equal(ModelPath, hookPath);
        Assert.Equal(OnnxDevice.Cpu, hookDevice);
    }

    [Fact]
    public void Explicit_cuda_without_a_runtime_fails_with_remediation_and_does_not_poison_the_cache()
    {
        var host = new OnnxModelHost();

        // The base CPU ORT package exports no CUDA entry point, so the typed appender throws regardless of
        // hardware → a clear remediation, never a raw interop crash.
        var ex = Assert.Throws<VoxaModelUnavailableException>(() => host.Load(ModelPath, OnnxDevice.Cuda));
        Assert.Contains("cuda", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Device=cpu", ex.Message);

        // The faulted load must not have poisoned the (path, cuda) cache key, and CPU still works.
        Assert.Throws<VoxaModelUnavailableException>(() => host.Load(ModelPath, OnnxDevice.Cuda));
        var ok = host.Load(ModelPath);
        Assert.Equal(OnnxDevice.Cpu, ok.ActiveDevice);
    }

    [Fact]
    public void Auto_never_throws_and_falls_back_to_cpu_without_a_gpu_runtime()
    {
        var host = new OnnxModelHost();

        // No GPU EP is available with the CPU-only base package, so Auto resolves to CPU (a warning is
        // logged, never a throw). With a GPU runtime added it would pick that EP instead.
        var session = host.Load(ModelPath, OnnxDevice.Auto);
        Assert.NotNull(session.Session);
        Assert.Equal(OnnxDevice.Cpu, session.ActiveDevice);
    }
}
