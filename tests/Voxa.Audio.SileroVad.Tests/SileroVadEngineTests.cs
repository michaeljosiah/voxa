using Voxa.Audio.SileroVad;

namespace Voxa.Audio.SileroVad.Tests;

public class SileroVadEngineTests
{
    [Fact]
    public void Constructs_With_16k_And_Reports_512_Window()
    {
        using var engine = new SileroVadEngine(16000);
        Assert.Equal(16000, engine.SampleRate);
        Assert.Equal(512, engine.WindowSize);
    }

    [Fact]
    public void Constructs_With_8k_And_Reports_256_Window()
    {
        using var engine = new SileroVadEngine(8000);
        Assert.Equal(8000, engine.SampleRate);
        Assert.Equal(256, engine.WindowSize);
    }

    [Fact]
    public void Throws_On_Unsupported_Sample_Rate()
    {
        Assert.Throws<ArgumentException>(() => new SileroVadEngine(44100));
    }

    [Fact]
    public void Probability_Returns_Value_In_Unit_Range()
    {
        using var engine = new SileroVadEngine(16000);
        var window = new float[engine.WindowSize]; // silence
        var p = engine.Probability(window);
        Assert.InRange(p, 0f, 1f);
    }

    [Fact]
    public void Silence_Yields_Low_Probability()
    {
        using var engine = new SileroVadEngine(16000);
        var window = new float[engine.WindowSize]; // pure zeros

        // First inference may be elevated as the LSTM warms up; average a handful of windows.
        float sum = 0;
        for (int i = 0; i < 10; i++) sum += engine.Probability(window);
        var avg = sum / 10f;

        Assert.True(avg < 0.3f, $"Expected silent-window probability average < 0.3, got {avg:F3}");
    }

    [Fact]
    public void Wrong_Window_Size_Throws()
    {
        using var engine = new SileroVadEngine(16000);
        Assert.Throws<ArgumentException>(() => engine.Probability(new float[100]));
        Assert.Throws<ArgumentException>(() => engine.Probability(new float[engine.WindowSize + 1]));
    }

    [Fact]
    public void Reset_Does_Not_Throw()
    {
        using var engine = new SileroVadEngine(16000);
        engine.Probability(new float[engine.WindowSize]);
        engine.Reset();
        engine.Probability(new float[engine.WindowSize]);
    }
}
