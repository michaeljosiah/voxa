using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Voxa.Audio.SileroVad;

/// <summary>
/// Thin wrapper around the Silero VAD v5 ONNX model. Stateful — each <see cref="Probability"/>
/// call carries the LSTM hidden state forward, so the model has memory of the previous ~250 ms.
/// </summary>
public sealed class SileroVadEngine : IDisposable
{
    private const string ResourceName = "Voxa.Audio.SileroVad.silero_vad.onnx";

    private readonly InferenceSession _session;
    private readonly long[] _srTensor;
    private readonly DenseTensor<long> _srWrapped;
    private DenseTensor<float> _state;

    /// <summary>Required input size for one inference. 512 at 16 kHz, 256 at 8 kHz.</summary>
    public int WindowSize { get; }

    /// <summary>Sample rate the engine was constructed for.</summary>
    public int SampleRate { get; }

    /// <param name="sampleRate">16000 or 8000. Other rates throw.</param>
    public SileroVadEngine(int sampleRate = 16000)
    {
        WindowSize = sampleRate switch
        {
            16000 => 512,
            8000 => 256,
            _ => throw new ArgumentException("Silero VAD v5 supports only 16000 or 8000 Hz.", nameof(sampleRate)),
        };
        SampleRate = sampleRate;

        var modelBytes = LoadEmbeddedModel();
        _session = new InferenceSession(modelBytes);

        _srTensor = new[] { (long)sampleRate };
        _srWrapped = new DenseTensor<long>(_srTensor, Array.Empty<int>());
        _state = new DenseTensor<float>(new[] { 2, 1, 128 });
    }

    /// <summary>Run one inference. Returns speech probability in [0, 1].</summary>
    /// <exception cref="ArgumentException">If <paramref name="window"/> length is not <see cref="WindowSize"/>.</exception>
    public float Probability(ReadOnlySpan<float> window)
    {
        if (window.Length != WindowSize)
        {
            throw new ArgumentException(
                $"Silero VAD requires exactly {WindowSize} samples per inference at {SampleRate} Hz; got {window.Length}.",
                nameof(window));
        }

        var inputTensor = new DenseTensor<float>(new[] { 1, WindowSize });
        var inputSpan = inputTensor.Buffer.Span;
        window.CopyTo(inputSpan);

        var inputs = new[]
        {
            NamedOnnxValue.CreateFromTensor("input", inputTensor),
            NamedOnnxValue.CreateFromTensor("state", _state),
            NamedOnnxValue.CreateFromTensor("sr", _srWrapped),
        };

        using var results = _session.Run(inputs);

        DenseTensor<float>? newState = null;
        float prob = 0f;
        foreach (var r in results)
        {
            if (r.Name == "stateN") newState = r.AsTensor<float>().ToDenseTensor();
            else if (r.Name == "output") prob = r.AsTensor<float>()[0, 0];
        }
        if (newState is not null) _state = newState;
        return prob;
    }

    /// <summary>Reset the LSTM hidden state. Call between sessions or to flush context.</summary>
    public void Reset()
    {
        _state = new DenseTensor<float>(new[] { 2, 1, 128 });
    }

    public void Dispose() => _session.Dispose();

    private static byte[] LoadEmbeddedModel()
    {
        using var stream = typeof(SileroVadEngine).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{ResourceName}' not found. The Silero VAD ONNX model didn't ship with this assembly.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
