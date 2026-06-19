using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Voxa.Speech;

namespace Voxa.Audio.SmartTurn.Tests;

/// <summary>
/// The sidecar smart-turn classifier and its stdio protocol. The real model runs in a Python process;
/// here a fake transport + a MemoryStream verify the C# half — threshold verdict, fail-safe-to-complete,
/// empty-audio short-circuit, and the request/response framing — with no process spawned.
/// </summary>
public class SidecarSmartTurnClassifierTests
{
    private sealed class FakeSidecar(double probability, bool throwOnPredict = false, int predictDelayMs = 0, int startDelayMs = 0)
        : ISmartTurnSidecar
    {
        public int Starts { get; private set; }
        public int Predicts { get; private set; }
        public int Disposes { get; private set; }

        public async Task StartAsync(CancellationToken ct)
        {
            Starts++;
            if (startDelayMs > 0) await Task.Delay(startDelayMs, ct);
        }

        public async Task<double> PredictAsync(ReadOnlyMemory<byte> pcm, int sampleRate, CancellationToken ct)
        {
            Predicts++;
            if (throwOnPredict) throw new InvalidOperationException("sidecar died");
            if (predictDelayMs > 0) await Task.Delay(predictDelayMs, ct);
            return probability;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Dispose() => Disposes++;
    }

    [Fact]
    public async Task Returns_Complete_When_Probability_Meets_The_Threshold()
    {
        var c = new SidecarSmartTurnClassifier(new SmartTurnOptions { Threshold = 0.5 }, new FakeSidecar(0.9));
        Assert.True(await c.IsTurnCompleteAsync(new byte[320], 16000, CancellationToken.None));
    }

    [Fact]
    public async Task Returns_Incomplete_Below_The_Threshold()
    {
        var c = new SidecarSmartTurnClassifier(new SmartTurnOptions { Threshold = 0.5 }, new FakeSidecar(0.2));
        Assert.False(await c.IsTurnCompleteAsync(new byte[320], 16000, CancellationToken.None));
    }

    [Fact]
    public async Task Fails_Complete_When_The_Sidecar_Throws()
    {
        var c = new SidecarSmartTurnClassifier(new SmartTurnOptions(), new FakeSidecar(0, throwOnPredict: true));
        Assert.True(await c.IsTurnCompleteAsync(new byte[320], 16000, CancellationToken.None));
    }

    [Fact]
    public async Task Empty_Audio_Is_Complete_Without_Calling_The_Sidecar()
    {
        var fake = new FakeSidecar(0.0);
        var c = new SidecarSmartTurnClassifier(new SmartTurnOptions(), fake);
        Assert.True(await c.IsTurnCompleteAsync(ReadOnlyMemory<byte>.Empty, 16000, CancellationToken.None));
        Assert.Equal(0, fake.Predicts);
    }

    [Fact]
    public async Task Starts_The_Sidecar_Once_Across_Calls()
    {
        var fake = new FakeSidecar(0.9);
        var c = new SidecarSmartTurnClassifier(new SmartTurnOptions { Threshold = 0.5 }, fake);
        await c.IsTurnCompleteAsync(new byte[320], 16000, CancellationToken.None);
        await c.IsTurnCompleteAsync(new byte[320], 16000, CancellationToken.None);
        Assert.Equal(1, fake.Starts);
        Assert.Equal(2, fake.Predicts);
    }

    [Fact]
    public void EncodeRequestHeader_Writes_The_Json_Line()
        => Assert.Equal("{\"sample_rate\":16000,\"bytes\":320}\n",
            Encoding.UTF8.GetString(SmartTurnSidecarProtocol.EncodeRequestHeader(16000, 320)));

    [Fact]
    public async Task ReadProbability_Parses_The_Response_Line()
    {
        using var s = new MemoryStream(Encoding.UTF8.GetBytes("{\"probability\":0.73}\n"));
        Assert.Equal(0.73, await SmartTurnSidecarProtocol.ReadProbabilityAsync(s, CancellationToken.None), 5);
    }

    [Fact]
    public async Task ReadProbability_Throws_On_An_Error_Response()
    {
        using var s = new MemoryStream(Encoding.UTF8.GetBytes("{\"error\":\"model missing\"}\n"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => SmartTurnSidecarProtocol.ReadProbabilityAsync(s, CancellationToken.None));
    }

    [Fact]
    public async Task ReadProbability_Throws_When_The_Sidecar_Closes_Early()
    {
        using var s = new MemoryStream();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => SmartTurnSidecarProtocol.ReadProbabilityAsync(s, CancellationToken.None));
    }

    // ── Codex P1: per-turn / startup timeouts must bound a hung or loading sidecar ──

    [Fact]
    public async Task A_Predict_That_Exceeds_The_Timeout_Fails_Complete()
    {
        var fake = new FakeSidecar(0.0, predictDelayMs: 5000);
        var c = new SidecarSmartTurnClassifier(new SmartTurnOptions { SidecarTimeoutMs = 50, Threshold = 0.5 }, fake);

        var sw = Stopwatch.StartNew();
        var complete = await c.IsTurnCompleteAsync(new byte[320], 16000, CancellationToken.None);
        sw.Stop();

        Assert.True(complete);                      // fail-safe to complete
        Assert.True(sw.ElapsedMilliseconds < 2000); // did not wait the full 5 s
    }

    [Fact]
    public async Task A_Startup_That_Exceeds_The_Ready_Timeout_Fails_Complete()
    {
        var fake = new FakeSidecar(0.9, startDelayMs: 5000);
        var c = new SidecarSmartTurnClassifier(new SmartTurnOptions { SidecarReadyTimeoutMs = 50 }, fake);

        var sw = Stopwatch.StartNew();
        var complete = await c.IsTurnCompleteAsync(new byte[320], 16000, CancellationToken.None);
        sw.Stop();

        Assert.True(complete);
        Assert.True(sw.ElapsedMilliseconds < 2000);
    }

    [Fact]
    public async Task A_User_Interruption_Propagates_Rather_Than_Failing_Complete()
    {
        var fake = new FakeSidecar(0.9, predictDelayMs: 5000);
        var c = new SidecarSmartTurnClassifier(new SmartTurnOptions(), fake);
        using var cts = new CancellationTokenSource(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await c.IsTurnCompleteAsync(new byte[320], 16000, cts.Token));
    }

    // ── Codex P2: the singleton must survive synchronous container teardown ──

    [Fact]
    public void Dispose_Is_Synchronous_And_Disposes_The_Sidecar()
    {
        var fake = new FakeSidecar(0.9);
        var c = new SidecarSmartTurnClassifier(new SmartTurnOptions(), fake);

        c.Dispose();   // StudioServices.Reconfigure → ServiceProvider.Dispose() is synchronous
        Assert.Equal(1, fake.Disposes);
    }

    [Fact] // Codex P2: ServiceProvider.Dispose() (sync) must not throw on a resolved Sidecar singleton.
    public void Sidecar_Singleton_Survives_Synchronous_Provider_Disposal()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Voxa:SmartTurn:Provider"] = "Sidecar",
            ["Voxa:SmartTurn:PythonScript"] = "sidecar/voxa_smart_turn_sidecar.py",
        }).Build();

        var sp = new ServiceCollection().AddVoxaSmartTurn(config).BuildServiceProvider();
        _ = sp.GetRequiredService<ISmartTurnClassifier>();   // resolved → the container tracks it for disposal
        sp.Dispose();                                        // would throw if it were IAsyncDisposable-only
    }

    // ── readiness handshake framing ──

    [Fact]
    public async Task ReadReady_Returns_When_The_Sidecar_Signals_Ready()
    {
        using var s = new MemoryStream(Encoding.UTF8.GetBytes("{\"ready\":true}\n"));
        await SmartTurnSidecarProtocol.ReadReadyAsync(s, CancellationToken.None); // no throw = ready
    }

    [Fact]
    public async Task ReadReady_Throws_On_A_Startup_Error()
    {
        using var s = new MemoryStream(Encoding.UTF8.GetBytes("{\"error\":\"deps missing\"}\n"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => SmartTurnSidecarProtocol.ReadReadyAsync(s, CancellationToken.None));
    }

    [Fact]
    public async Task ReadReady_Throws_When_The_Sidecar_Exits_Before_Ready()
    {
        using var s = new MemoryStream();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => SmartTurnSidecarProtocol.ReadReadyAsync(s, CancellationToken.None));
    }
}
