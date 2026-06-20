using Microsoft.Extensions.Logging.Abstractions;

namespace Voxa.AspNetCore.Tests;

/// <summary>
/// VRT-002 tuning resolution: the new robustness knobs are off in Default (defaults-byte-identity gate),
/// on in the latency profiles, overridable by explicit config, and a meaningless EagerSttDelay ≥ StopDuration
/// is clamped off.
/// </summary>
public class VoxaTuningResolverVrt002Tests
{
    private static VoxaEffectiveTuning Resolve(VoxaOptions o)
        => new VoxaTuningResolver(NullLogger<VoxaTuningResolver>.Instance).Resolve(o);

    [Fact]
    public void Default_Profile_Leaves_All_Vrt002_Knobs_Off()
    {
        var t = Resolve(new VoxaOptions { Profile = "Default" });

        Assert.Null(t.VadEagerSttDelay);
        Assert.Null(t.VadMaxUtteranceDuration);
        Assert.Null(t.MaxResponseDuration);
    }

    [Fact]
    public void LowLatency_Profile_Enables_Eager_And_The_Caps()
    {
        var t = Resolve(new VoxaOptions { Profile = "LowLatency" });

        Assert.Equal(TimeSpan.FromMilliseconds(150), t.VadEagerSttDelay);
        Assert.Equal(TimeSpan.FromSeconds(20), t.VadMaxUtteranceDuration);
        Assert.Equal(TimeSpan.FromSeconds(30), t.MaxResponseDuration);
        Assert.True(t.VadEagerSttDelay < t.VadStopDuration); // eager must fire before the gate closes
    }

    [Fact]
    public void Explicit_Config_Overrides_The_Profile()
    {
        var t = Resolve(new VoxaOptions
        {
            Profile = "Default",
            Vad = new VoxaVadOptions { EagerSttDelayMs = 200, StopDurationMs = 600, MaxUtteranceDurationMs = 15000 },
            Agent = new VoxaAgentOptions { MaxResponseDurationMs = 45000 },
        });

        Assert.Equal(TimeSpan.FromMilliseconds(200), t.VadEagerSttDelay);
        Assert.Equal(TimeSpan.FromSeconds(15), t.VadMaxUtteranceDuration);
        Assert.Equal(TimeSpan.FromSeconds(45), t.MaxResponseDuration);
    }

    [Fact]
    public void EagerSttDelay_At_Or_Above_StopDuration_Is_Clamped_Off()
    {
        var t = Resolve(new VoxaOptions
        {
            Profile = "Default",
            Vad = new VoxaVadOptions { EagerSttDelayMs = 800, StopDurationMs = 800 },
        });

        Assert.Null(t.VadEagerSttDelay); // meaningless ⇒ disabled (the VAD also guards this at runtime)
    }
}
