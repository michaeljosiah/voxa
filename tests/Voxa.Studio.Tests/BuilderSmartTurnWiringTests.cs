using Microsoft.Extensions.DependencyInjection;
using Voxa.Speech;
using Voxa.Studio.Services;

namespace Voxa.Studio.Tests;

/// <summary>
/// Codex: the "Run from canvas" path builds the VAD directly (not via DefaultVoicePipelineComposer), so it
/// must mirror the composer's smart-turn wiring — a registered classifier is bound to the VAD's
/// ConfirmTurnEnd, and with none registered the settings are unchanged (classic silence behavior).
/// </summary>
public class BuilderSmartTurnWiringTests
{
    private sealed class StubClassifier : ISmartTurnClassifier
    {
        public ValueTask<bool> IsTurnCompleteAsync(ReadOnlyMemory<byte> pcm, int sampleRate, CancellationToken ct)
            => ValueTask.FromResult(true);
    }

    private static VoxaVadSettings Settings() => new(
        SampleRate: 16000, ConfidenceThreshold: 0.5f, MinRms: 0.0,
        StartDuration: TimeSpan.FromMilliseconds(100),
        StopDuration: TimeSpan.FromMilliseconds(200),
        PrerollDuration: TimeSpan.FromMilliseconds(300));

    [Fact]
    public void Wires_ConfirmTurnEnd_When_A_Classifier_Is_Registered()
    {
        using var sp = new ServiceCollection()
            .AddSingleton<ISmartTurnClassifier>(new StubClassifier()).BuildServiceProvider();
        Assert.NotNull(BuilderChainCompiler.WithSmartTurn(sp, Settings()).ConfirmTurnEnd);
    }

    [Fact]
    public void Leaves_ConfirmTurnEnd_Null_When_No_Classifier_Is_Registered()
    {
        using var sp = new ServiceCollection().BuildServiceProvider();
        Assert.Null(BuilderChainCompiler.WithSmartTurn(sp, Settings()).ConfirmTurnEnd);
    }
}
