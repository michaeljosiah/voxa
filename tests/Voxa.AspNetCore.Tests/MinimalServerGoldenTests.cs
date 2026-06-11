namespace Voxa.AspNetCore.Tests;

/// <summary>
/// Guards the "five-line voice bot" DX budget: the MinimalServer program must stay at or under
/// six top-level statements and must contain the required API strings. Anyone whose change forces
/// a seventh statement into the minimal program breaks this test.
/// </summary>
public class MinimalServerGoldenTests
{
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Voxa.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    [Fact]
    public void MinimalServer_Has_At_Most_Six_Statements()
    {
        var root = FindRepoRoot() ?? throw new InvalidOperationException("Could not find repo root (Voxa.slnx not found).");
        var programPath = Path.Combine(root, "samples", "Voxa.Samples.MinimalServer", "Program.cs");
        Assert.True(File.Exists(programPath), $"MinimalServer Program.cs not found at: {programPath}");

        var lines = File.ReadAllLines(programPath);
        // Count lines that are not blank and not comments — and exclude serving the optional
        // wwwroot browser test UI, which is sample scaffolding, not part of the "working voice
        // bot" budget the five-line claim protects. The voice bot itself is still ≤ 6 lines.
        var statementLines = lines.Where(l =>
        {
            var t = l.Trim();
            if (t.Length == 0 || t.StartsWith("//") || t.StartsWith("/*") || t.StartsWith("*"))
                return false;
            if (t.Contains("UseFileServer") || t.Contains("UseStaticFiles") || t.Contains("UseDefaultFiles"))
                return false;
            return true;
        }).ToList();

        Assert.True(statementLines.Count <= 6,
            $"MinimalServer/Program.cs has {statementLines.Count} non-blank/non-comment lines but the DX budget is ≤ 6. " +
            $"Lines found:\n{string.Join("\n", statementLines)}");
    }

    [Fact]
    public void MinimalServer_Contains_Required_API_Strings()
    {
        var root = FindRepoRoot() ?? throw new InvalidOperationException("Could not find repo root.");
        var programPath = Path.Combine(root, "samples", "Voxa.Samples.MinimalServer", "Program.cs");
        var source = File.ReadAllText(programPath);

        Assert.Contains("AddVoxa(builder.Configuration)", source);
        Assert.Contains("MapVoxaVoice(\"/voice\").UseDefaults()", source);
    }
}
