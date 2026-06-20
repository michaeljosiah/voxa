namespace Voxa.Audio;

/// <summary>
/// Vendor-neutral spectral-enhancement (denoise) seam (VLS-004): cleans the near-end mic signal *before* the VAD
/// and STT see it, so on-device transcription holds up in noisy or reverberant rooms. Mirrors speech-core's
/// <c>EnhancerInterface</c>; satisfied by an in-process ONNX engine (a future <c>Voxa.Audio.Enhance</c> package)
/// or any future DSP. Per-session and single-threaded — the processor's data loop calls <see cref="Enhance"/>
/// from one thread, exactly like the Silero VAD engine.
/// <para>
/// This ships the <b>seam + a passthrough default</b>, not a model. The composer inserts the enhancer stage
/// after the AEC stage (VRT-003) and before the VAD; with <c>Voxa:Enhance:Engine</c> unset / "None" it inserts
/// nothing, so the pipeline is byte-identical to before this seam existed.
/// </para>
/// </summary>
public interface IAudioEnhancer : IDisposable
{
    /// <summary>
    /// The PCM sample rate (Hz) the model/DSP expects — the near-end / input rate it runs at.
    /// <see cref="AudioEnhancerProcessor"/> checks each inbound <c>AudioRawFrame.SampleRate</c> against this; on a
    /// mismatch it surfaces a clear error (an upstream <c>ErrorFrame</c>) and forwards the frame <b>unenhanced</b>
    /// — it never silently resamples or feeds wrong-rate PCM to a fixed-rate model (which would corrupt what the
    /// VAD/STT see). Any internal resample an engine wants to do happens behind its own advertised rate.
    /// </summary>
    int SampleRate { get; }

    /// <summary>
    /// Clean one chunk of 16-bit mono PCM and return the result. MUST return the SAME byte length / sample rate
    /// / channel layout it was given — this is signal conditioning, not a format change, so the frame's duration
    /// semantics are preserved. Called many times per second on the audio hot path; keep it allocation-light
    /// (reuse buffers, like the Silero engine). May return the input unchanged (passthrough).
    /// </summary>
    ReadOnlyMemory<byte> Enhance(ReadOnlyMemory<byte> pcm);

    /// <summary>Drop any internal/recurrent state between sessions (cf. <c>SileroVadEngine.Reset()</c>).</summary>
    void Reset();
}

/// <summary>
/// The default enhancer: returns the mic audio untouched and holds no state, so a pipeline built with it is
/// byte-identical to one built before this seam existed. The default composition omits the stage entirely when
/// <c>Voxa:Enhance:Engine</c> is unset / "None" (it never even inserts this passthrough); this exists mainly for
/// tests and for hosts that resolve an <see cref="IAudioEnhancer"/> explicitly.
/// </summary>
public sealed class NullAudioEnhancer : IAudioEnhancer
{
    public NullAudioEnhancer(int sampleRate) => SampleRate = sampleRate;

    public int SampleRate { get; }
    public ReadOnlyMemory<byte> Enhance(ReadOnlyMemory<byte> pcm) => pcm; // passthrough
    public void Reset() { }
    public void Dispose() { }
}
