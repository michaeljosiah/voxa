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
            // A sample-rate mismatch is a configuration error: the chosen enhancer's rate doesn't match the
            // route. Feeding a fixed-rate denoiser wrong-rate PCM would corrupt what the VAD/STT see, so we fail
            // fast — surface a clear error and do NOT enhance, forward, or silently resample. PushErrorAsync
            // emits an upstream ErrorFrame, which PipelineRunner turns into a session failure (the contract's
            // "fail at session start"). The flag keeps a torn-down pipeline from emitting it on every frame.
            // (An engine that accepts multiple rates resamples internally behind its own advertised SampleRate.)
            if (audio.SampleRate != _enhancer.SampleRate)
            {
                if (!_rateMismatchReported)
                {
                    _rateMismatchReported = true;
                    await PushErrorAsync(
                        $"AudioEnhancer: input is {audio.SampleRate} Hz but the configured enhancer expects " +
                        $"{_enhancer.SampleRate} Hz. Configure a matching enhancer or input sample rate.",
                        null, ct).ConfigureAwait(false);
                }
                return; // never feed wrong-rate PCM to the model, and don't propagate it onward
            }

            await PushFrameAsync(audio with { Pcm = _enhancer.Enhance(audio.Pcm) }, ct).ConfigureAwait(false);
            return;
        }

        await PushFrameAsync(frame, ct).ConfigureAwait(false); // forward Start/End/system/everything else
    }
}
