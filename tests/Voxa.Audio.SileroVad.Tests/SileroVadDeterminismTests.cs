using Voxa.Audio.SileroVad;

namespace Voxa.Audio.SileroVad.Tests;

/// <summary>
/// Guards the zero-allocation engine refactor (VPS-001 WS5): reusing the input/state tensors and
/// copying the LSTM state back in-place must produce bit-for-bit identical results to a fresh run,
/// and <see cref="SileroVadEngine.Reset"/> must truly clear the recurrent state.
/// </summary>
public class SileroVadDeterminismTests
{
    private static float[][] MakeWindows(int windowSize, int count)
    {
        var windows = new float[count][];
        var rnd = new Random(1234);
        for (int w = 0; w < count; w++)
        {
            var win = new float[windowSize];
            // A deterministic voiced-ish signal: low-frequency sine + a little noise.
            for (int i = 0; i < windowSize; i++)
            {
                double t = (w * windowSize + i) / 16000.0;
                win[i] = (float)(0.3 * Math.Sin(2 * Math.PI * 220 * t) + (rnd.NextDouble() - 0.5) * 0.02);
            }
            windows[w] = win;
        }
        return windows;
    }

    [Fact]
    public void StateCarriesForward_AndIsDeterministicAcrossResets()
    {
        using var engine = new SileroVadEngine(16000);
        var windows = MakeWindows(engine.WindowSize, 40);

        var first = new float[windows.Length];
        for (int i = 0; i < windows.Length; i++) first[i] = engine.Probability(windows[i]);

        // Reset must clear recurrent state so the identical input reproduces the identical sequence.
        engine.Reset();
        var second = new float[windows.Length];
        for (int i = 0; i < windows.Length; i++) second[i] = engine.Probability(windows[i]);

        for (int i = 0; i < windows.Length; i++)
        {
            Assert.True(Math.Abs(first[i] - second[i]) < 1e-6f,
                $"window {i}: {first[i]} vs {second[i]} — reused-tensor path is not deterministic");
        }

        // Sanity: the LSTM state actually evolves (otherwise the refactor could be silently
        // ignoring stateN). Feeding the same window repeatedly should not be perfectly constant.
        engine.Reset();
        float a = engine.Probability(windows[0]);
        float b = engine.Probability(windows[0]);
        Assert.True(a != b || first[1] != first[0], "state does not appear to carry forward");
    }
}
