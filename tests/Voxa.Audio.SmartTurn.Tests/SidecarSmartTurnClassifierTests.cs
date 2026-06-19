using System.Text;

namespace Voxa.Audio.SmartTurn.Tests;

/// <summary>
/// The sidecar smart-turn classifier and its stdio protocol. The real model runs in a Python process;
/// here a fake transport + a MemoryStream verify the C# half — threshold verdict, fail-safe-to-complete,
/// empty-audio short-circuit, and the request/response framing — with no process spawned.
/// </summary>
public class SidecarSmartTurnClassifierTests
{
    private sealed class FakeSidecar(double probability, bool throwOnPredict = false) : ISmartTurnSidecar
    {
        public int Starts { get; private set; }
        public int Predicts { get; private set; }

        public Task StartAsync(CancellationToken ct) { Starts++; return Task.CompletedTask; }

        public Task<double> PredictAsync(ReadOnlyMemory<byte> pcm, int sampleRate, CancellationToken ct)
        {
            Predicts++;
            if (throwOnPredict) throw new InvalidOperationException("sidecar died");
            return Task.FromResult(probability);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
}
