namespace Voxa.Studio.Audio;

/// <summary>
/// Streaming linear-interpolation resampler for PCM16 mono. Voice-bandwidth quality is ample
/// here: the capture path feeds a 16 kHz STT model (hardware is typically 44.1/48 k, so this is
/// a downsample) and the render path upsamples synthesized speech to the device rate. Stateful —
/// keeps the last input sample so chunk boundaries don't click. Not thread-safe; use one
/// instance per direction.
/// </summary>
public sealed class LinearResampler
{
    private readonly double _step;   // input samples consumed per output sample
    private double _phase;           // fractional read position relative to _previous
    private short _previous;
    private bool _primed;

    public LinearResampler(int inputRate, int outputRate)
    {
        if (inputRate <= 0) throw new ArgumentOutOfRangeException(nameof(inputRate));
        if (outputRate <= 0) throw new ArgumentOutOfRangeException(nameof(outputRate));
        InputRate = inputRate;
        OutputRate = outputRate;
        _step = (double)inputRate / outputRate;
    }

    public int InputRate { get; }
    public int OutputRate { get; }
    public bool IsPassthrough => InputRate == OutputRate;

    /// <summary>Upper bound of output samples produced for <paramref name="inputSamples"/> input samples.</summary>
    public int MaxOutputSamples(int inputSamples)
        => (int)Math.Ceiling((inputSamples + 1) / _step) + 1;

    /// <summary>
    /// Resample <paramref name="input"/> into <paramref name="output"/>; returns samples written.
    /// </summary>
    public int Process(ReadOnlySpan<short> input, Span<short> output)
    {
        if (IsPassthrough)
        {
            input.CopyTo(output);
            return input.Length;
        }

        if (input.IsEmpty) return 0;

        if (!_primed)
        {
            _previous = input[0];
            _primed = true;
        }

        int written = 0;
        // _phase is the output cursor in input-sample units, measured from _previous (index -1).
        while (true)
        {
            // The interpolation interval is [floor(phase)-1, floor(phase)] in input indices,
            // where index -1 is _previous from the prior chunk.
            int idx = (int)Math.Floor(_phase);
            if (idx >= input.Length) break;

            short s0 = idx == 0 ? _previous : input[idx - 1];
            short s1 = input[idx];
            double frac = _phase - idx;
            output[written++] = (short)(s0 + (s1 - s0) * frac);
            _phase += _step;
        }

        _phase -= input.Length;
        _previous = input[^1];
        return written;
    }

    /// <summary>Reset boundary state (e.g. after a playback flush) so stale samples don't smear in.</summary>
    public void Reset()
    {
        _phase = 0;
        _primed = false;
        _previous = 0;
    }
}
