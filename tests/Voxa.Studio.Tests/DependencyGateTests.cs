namespace Voxa.Studio.Tests;

/// <summary>
/// The VLS-001 §3.3 GPL rule, extended to the Studio closure (VST-001 WS6): Studio pulls every
/// local engine, so its output directory is exactly where a contaminating package (KokoroSharp's
/// linked espeak-ng natives) would land. espeak-ng stays an out-of-process CLI; NAudio (MIT) and
/// Avalonia (MIT) are the only UI-side native-adjacent additions.
/// </summary>
public class DependencyGateTests
{
    [Fact]
    public void Studio_Assembly_References_No_KokoroSharp_Or_Espeak()
    {
        var referenced = typeof(Voxa.Studio.App).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(referenced, a =>
            a.Name!.Contains("KokoroSharp", StringComparison.OrdinalIgnoreCase) ||
            a.Name!.Contains("espeak", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void No_Espeak_Native_Is_Copied_Into_Studios_Output()
    {
        // This test project's output is the Studio app's full closure (app + all engines + UI).
        var files = Directory.EnumerateFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .ToArray();
        Assert.DoesNotContain(files, f =>
            f!.Contains("espeak", StringComparison.OrdinalIgnoreCase) ||
            f.Contains("KokoroSharp", StringComparison.OrdinalIgnoreCase));
    }
}
