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
            await PushFrameAsync(audio with { Pcm = _enhancer.Enhance(audio.Pcm) }, ct).ConfigureAwait(false);
        else
            await PushFrameAsync(frame, ct).ConfigureAwait(false); // forward Start/End/system/everything else
    }
}
