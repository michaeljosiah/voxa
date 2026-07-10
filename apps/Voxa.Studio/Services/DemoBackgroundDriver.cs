using System.Runtime.CompilerServices;
using Voxa.Frames;
using Voxa.Processors;

namespace Voxa.Studio.Services;

/// <summary>
/// Keyless demo "thinker" for canvas runs (VDX-008): waits a configurable few seconds — long enough
/// to hear the talker keep the floor — then returns a canned research note that names itself as a
/// demo, so nobody mistakes it for real research. Swap the node's provider to OpenAI for a real one.
/// </summary>
internal sealed class DemoBackgroundDriver : IAgentTurnDriver
{
    private readonly TimeSpan _delay;

    public DemoBackgroundDriver(TimeSpan delay) => _delay = delay;

    public async IAsyncEnumerable<Frame> RunTurnAsync(
        VoiceTurnContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new StatusFrame("Background task working…");
        await Task.Delay(_delay, ct);
        yield return new LlmTextChunkFrame(
            $"Demo background note on \"{ctx.UserText}\": this canned answer took {_delay.TotalSeconds:0} seconds " +
            "on purpose — the conversation kept flowing while it ran. Switch the Background agent node to " +
            "OpenAI for real research.");
    }
}
