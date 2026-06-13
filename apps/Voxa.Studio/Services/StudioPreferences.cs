using System.Text.Json;

namespace Voxa.Studio.Services;

/// <summary>
/// Small, non-sensitive Studio preferences (VST-003): currently just the selected theme id, persisted
/// to <c>~/voxa-studio-prefs.json</c>. Corrupt/absent file → defaults, never throws. A constructor
/// override is the test seam (no real file touched).
/// </summary>
public sealed class StudioPreferences
{
    private readonly string _path;

    public StudioPreferences(string? path = null) =>
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "voxa-studio-prefs.json");

    public string ThemeId { get; set; } = "warm";

    public static StudioPreferences Load(string? path = null)
    {
        var prefs = new StudioPreferences(path);
        try
        {
            if (File.Exists(prefs._path) &&
                JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(prefs._path)) is { } snap &&
                !string.IsNullOrWhiteSpace(snap.ThemeId))
                prefs.ThemeId = snap.ThemeId;
        }
        catch (Exception ex) when (ex is JsonException or IOException) { /* defaults */ }
        return prefs;
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(new Snapshot(ThemeId)));
        }
        catch (IOException) { /* a failed pref write must never crash the app */ }
    }

    private sealed record Snapshot(string ThemeId);
}
