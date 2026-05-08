using Voxa.Frames;

namespace Voxa.Pipelines;

/// <summary>
/// Thrown by <see cref="PipelineRunner.WaitAsync"/> when an <see cref="ErrorFrame"/> bubbles up
/// to the source. The original frame is preserved in <see cref="ErrorFrame"/>.
/// </summary>
public sealed class PipelineFailedException : Exception
{
    public ErrorFrame ErrorFrame { get; }

    public PipelineFailedException(ErrorFrame errorFrame)
        : base(errorFrame?.Message ?? "Pipeline failed.", errorFrame?.InnerException)
    {
        ErrorFrame = errorFrame ?? throw new ArgumentNullException(nameof(errorFrame));
    }
}
