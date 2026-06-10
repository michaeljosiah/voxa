using Microsoft.ML.OnnxRuntime;

namespace Voxa.Audio.SileroVad;

/// <summary>
/// Thin wrapper around the Silero VAD v5 ONNX model. Stateful — each <see cref="Probability"/>
/// call carries the LSTM hidden state forward, so the model has memory of the previous ~250 ms.
///
/// <para>
/// Zero-allocation steady state: input, state, and output tensors are all wrapped in
/// <see cref="OrtValue"/>s created once and reused, and inference runs through the pre-bound
/// <see cref="InferenceSession.Run(RunOptions, IReadOnlyCollection{string}, IReadOnlyCollection{OrtValue}, IReadOnlyCollection{string}, IReadOnlyCollection{OrtValue})"/>
/// overload so ORT never materializes a result collection. Nothing is allocated per inference.
/// </para>
///
/// <para>
/// NOT thread-safe. The reused buffers mean <see cref="Probability"/> must be called from a single
/// thread (the VAD processor's data loop does exactly this). The LSTM state is inherently ordered
/// anyway.
/// </para>
/// </summary>
public sealed class SileroVadEngine : IDisposable
{
    private const string ResourceName = "Voxa.Audio.SileroVad.silero_vad.onnx";

    private readonly InferenceSession _session;
    private readonly RunOptions _runOptions;

    // Pinned managed backing buffers — the OrtValues below read/write these directly.
    private readonly float[] _inputBuf;
    private readonly float[] _stateBuf;       // current state (model input)
    private readonly float[] _stateOutBuf;    // next state (model output); copied into _stateBuf after each run
    private readonly float[] _outBuf;         // probability (model output)

    private readonly OrtValue _inputValue;
    private readonly OrtValue _stateValue;
    private readonly OrtValue _srValue;
    private readonly OrtValue _outValue;
    private readonly OrtValue _stateOutValue;

    private readonly string[] _inputNames = { "input", "state", "sr" };
    private readonly string[] _outputNames = { "output", "stateN" };
    private readonly OrtValue[] _inputValues;
    private readonly OrtValue[] _outputValues;

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
        using var sessionOptions = new SessionOptions
        {
            // Tiny (~1 MB) model: single-threaded sequential inference is faster than ONNX
            // fanning out an intra-op pool, and it doesn't steal cores from the audio pipeline.
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        _session = new InferenceSession(modelBytes, sessionOptions);
        _runOptions = new RunOptions();

        const int stateLen = 2 * 1 * 128;
        _inputBuf = new float[WindowSize];
        _stateBuf = new float[stateLen];
        _stateOutBuf = new float[stateLen];
        _outBuf = new float[1];
        var srBuf = new long[] { sampleRate };

        // OrtValues wrap the managed arrays in place (pinned for the OrtValue's lifetime). Created
        // once and reused — the contents of _inputBuf / _stateBuf are updated before each Run.
        _inputValue = OrtValue.CreateTensorValueFromMemory(_inputBuf, new long[] { 1, WindowSize });
        _stateValue = OrtValue.CreateTensorValueFromMemory(_stateBuf, new long[] { 2, 1, 128 });
        _srValue = OrtValue.CreateTensorValueFromMemory(srBuf, new long[] { 1 });
        _outValue = OrtValue.CreateTensorValueFromMemory(_outBuf, new long[] { 1, 1 });
        _stateOutValue = OrtValue.CreateTensorValueFromMemory(_stateOutBuf, new long[] { 2, 1, 128 });

        _inputValues = new[] { _inputValue, _stateValue, _srValue };
        _outputValues = new[] { _outValue, _stateOutValue };
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

        // Copy the window into the pinned input buffer the input OrtValue points at.
        window.CopyTo(_inputBuf);

        // Pre-bound inputs AND outputs — ORT writes straight into _outBuf / _stateOutBuf and
        // allocates nothing (no DisposableNamedOnnxValue result collection).
        _session.Run(_runOptions, _inputNames, _inputValues, _outputNames, _outputValues);

        // Carry the new LSTM state forward for the next inference. Single-threaded, so this
        // completes before the next Run reads _stateBuf.
        Array.Copy(_stateOutBuf, _stateBuf, _stateOutBuf.Length);

        return _outBuf[0];
    }

    /// <summary>Reset the LSTM hidden state. Call between sessions or to flush context.</summary>
    public void Reset()
    {
        Array.Clear(_stateBuf);
        Array.Clear(_stateOutBuf);
    }

    public void Dispose()
    {
        _inputValue.Dispose();
        _stateValue.Dispose();
        _srValue.Dispose();
        _outValue.Dispose();
        _stateOutValue.Dispose();
        _runOptions.Dispose();
        _session.Dispose();
    }

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
