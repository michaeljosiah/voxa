namespace Voxa.Speech.Voxtral.Tests;

/// <summary>The GPU gate is pure over an injected probe — no shelling out to nvidia-smi.</summary>
public class VoxtralCapabilityProbeTests
{
    private sealed class FakeGpu(int gb) : IGpuInfoProbe
    {
        public int LargestGpuMemoryGb() => gb;
    }

    [Theory]
    [InlineData(24, 16, true)]
    [InlineData(16, 16, true)]   // exactly at the floor is capable
    [InlineData(15, 16, false)]  // just under is not
    [InlineData(0, 16, false)]   // no GPU detected
    public void Gates_On_The_Vram_Floor(int gpuGb, int minGb, bool expected)
        => Assert.Equal(expected, new VoxtralCapabilityProbe(new FakeGpu(gpuGb)).IsCapable(minGb));
}
