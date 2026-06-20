using Voxa.Frames;
using Voxa.Processors;

namespace Voxa.Audio;

/// <summary>
/// Runs an <see cref="IAudioEnhancer"/> over the near-end mic stream (VLS-004). Placed by the composer after the
/// AEC stage (VRT-003) and before the VAD: on each <see cref="AudioRawFrame"/> it returns a frame carrying the
/// cleaned PCM (same length / rate / channels — or, for <see cref="NullAudioEnhancer"/>, the same buffer). Every
/// other frame is forwarded unchanged, including <see cref="StartFrame"/>/<see cref="EndFrame"/> (or the sink
/// never completes). Denoising ahead of detection means the VAD's probability/energy gate and the STT engine
/// both see the cleaned signal.
/// </summary>
public sealed class AudioEnhancerProcessor : FrameProcessor
{
    private readonly IAudioEnhancer _enhancer;
    private bool _rateMismatchReported; // surface the rate-mismatch error once, not per frame

    public AudioEnhancerProcessor(IAudioEnhancer enhancer) : base("AudioEnhancer")
        => _enhancer = enhancer ?? throw new ArgumentNullException(nameof(enhancer));

    protected override ValueTask OnStartAsync(StartFrame frame, CancellationToken ct)
    {
        _enhancer.Reset(); // fresh denoiser state for the session; StartFrame itself is forwarded below
        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnEndAsync(EndFrame frame, CancellationToken ct)
    {
        _enhancer.Dispose(); // release the model/DSP session; EndFrame is forwarded by ProcessFrameAsync
        return ValueTask.CompletedTask;
    }

    protected override async ValueTask ProcessFrameAsync(Frame frame, CancellationToken ct)
    {
        if (frame is AudioRawFrame audio)
        {
            // The enhancer expects exactly _enhancer.SampleRate. Feeding a fixed-rate denoiser audio at a
            // different rate would make it misread the timing and corrupt what the VAD/STT see, so on a mismatch
            // we surface a clear error (once) and forward the frame UNENHANCED — never a silent resample, never
            // corruption. (Resampling, if a real engine wants it, is the implementation's job behind its own rate.)
            if (audio.SampleRate != _enhancer.SampleRate)
            {
                if (!_rateMismatchReported)
                {
                    _rateMismatchReported = true;
                    await PushErrorAsync(
                        $"AudioEnhancer: input is {audio.SampleRate} Hz but the enhancer expects {_enhancer.SampleRate} Hz; " +
                        "forwarding audio unenhanced. Configure a matching enhancer or input sample rate.",
                        null, ct).ConfigureAwait(false);
                }
                await PushFrameAsync(frame, ct).ConfigureAwait(false); // unenhanced, original envelope
                return;
            }

            await PushFrameAsync(audio with { Pcm = _enhancer.Enhance(audio.Pcm) }, ct).ConfigureAwait(false);
            return;
        }

        await PushFrameAsync(frame, ct).ConfigureAwait(false); // forward Start/End/system/everything else
    }
}
