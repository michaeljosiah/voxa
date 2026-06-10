using BenchmarkDotNet.Attributes;
using Voxa.Audio.SileroVad;

namespace Voxa.Benchmarks;

/// <summary>
/// Per-inference Silero VAD cost. After WS5 the input/state tensors and the input array are reused
/// across calls (≈ 0 B/op); before WS5 each call allocated a fresh input DenseTensor, a
/// NamedOnnxValue[], and cloned the output state tensor.
/// </summary>
[MemoryDiagnoser]
public class VadBenchmarks
{
    private SileroVadEngine _engine = null!;
    private float[] _window = null!;

    [GlobalSetup]
    public void Setup()
    {
        _engine = new SileroVadEngine(16000);
        _window = new float[_engine.WindowSize];
        var rnd = new Random(42);
        for (int i = 0; i < _window.Length; i++)
            _window[i] = (float)(rnd.NextDouble() * 0.2 - 0.1);
    }

    [Benchmark]
    public float Probability() => _engine.Probability(_window);

    [GlobalCleanup]
    public void Cleanup() => _engine.Dispose();
}
