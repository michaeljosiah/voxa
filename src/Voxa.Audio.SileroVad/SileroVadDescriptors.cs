using Voxa.Speech;

namespace Voxa.Audio.SileroVad;

/// <summary>
/// Config-driven descriptor for Silero VAD.
/// Register via VoxaBuilder.AddProvider() or through the Voxa meta-package.
/// </summary>
public static class SileroVadDescriptors
{
    public static VoxaVadDescriptor Vad { get; } = new(
        Name: "Silero",
        CreateProcessor: (sp, settings) => new SileroVadProcessor(new SileroVadOptions
        {
            SampleRate           = settings.SampleRate,
            ConfidenceThreshold  = settings.ConfidenceThreshold,
            MinRms               = settings.MinRms,
            StartDuration        = settings.StartDuration,
            StopDuration         = settings.StopDuration,
            PrerollDuration      = settings.PrerollDuration,
            ProbabilityObserver  = settings.ProbabilityObserver,
            ConfirmTurnEnd       = settings.ConfirmTurnEnd,
        }));
}
