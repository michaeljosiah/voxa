using Microsoft.Extensions.Logging.Abstractions;

namespace Voxa.Speech.Voxtral.Tests;

/// <summary>
/// Mirrors the VVL-002 sidecar tests: covers the parts that need no real vLLM server. The managed readiness
/// poll and process-tree kill are exercised manually against a real vLLM — like the sidecar's frozen binary,
/// they aren't unit-tested because spawning the heavy runtime in CI isn't worth the flakiness.
/// </summary>
public class VoxtralServerProcessTests
{
    [Fact]
    public async Task ConnectOnly_Returns_The_Realtime_Endpoint_And_Launches_Nothing()
    {
        var options = new VoxtralOptions { ServerUrl = "ws://127.0.0.1:9999" };
        await using var server = new VoxtralServerProcess(options, NullLogger.Instance);

        var endpoint = await server.StartAsync(CancellationToken.None);

        Assert.Equal("ws://127.0.0.1:9999/v1/realtime", endpoint.ToString());
        // Disposing (via await using) in connect-only mode touches no process and must not throw.
    }

    [Fact] // codex P2: ServerUrl wins over a managed launch target — configuring both stays connect-only (no process)
    public async Task ServerUrl_Wins_Over_A_Managed_Launch_Target_And_Starts_No_Process()
    {
        var options = new VoxtralOptions
        {
            ServerUrl = "ws://127.0.0.1:9999",
            LaunchCommand = "voxtral-bogus-launcher-should-never-run", // would throw if a launch were attempted
        };
        await using var server = new VoxtralServerProcess(options, NullLogger.Instance);

        var endpoint = await server.StartAsync(CancellationToken.None); // must connect-only, not launch

        Assert.Equal("ws://127.0.0.1:9999/v1/realtime", endpoint.ToString());
    }

    [Fact]
    public async Task Managed_Launch_Of_A_Missing_Executable_Fails_With_Guidance()
    {
        var options = new VoxtralOptions { ExecutablePath = "/no/such/voxtral-bogus-launcher" };
        await using var server = new VoxtralServerProcess(options, NullLogger.Instance);

        var ex = await Assert.ThrowsAsync<VoxaModelUnavailableException>(
            () => server.StartAsync(CancellationToken.None));
        Assert.Contains("launch", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
