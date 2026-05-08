using Voxa.Frames;
using Voxa.Processors;

namespace Voxa.Testing.Processors;

/// <summary>Forwards every frame unchanged. Useful as a placeholder when wiring up tests.</summary>
public sealed class PassthroughProcessor : FrameProcessor
{
    public PassthroughProcessor(string name = "Passthrough") : base(name) { }

    protected override ValueTask ProcessFrameAsync(Frame frame, CancellationToken ct)
        => PushFrameAsync(frame, ct);
}
