using System.Text.Json;

namespace Voxa.Studio.Services;

/// <summary>A named pipeline: the flat Voxa config pairs a default-shape pipeline needs (no secrets).</summary>
public sealed record PipelineProfile(string Name, IReadOnlyDictionary<string, string?> Pairs);

/// <summary>
/// Named pipeline profiles + which one is active, persisted to <c>~/voxa-pipelines.json</c> (Builder
/// Phase 2). A profile is the flat <c>Voxa:*</c> selection (providers, models, latency profile) a
/// default-shape pipeline composes from — <b>never API keys</b> (those live in the DPAPI secrets layer),
/// so the file is safe to share. Corrupt/absent file → empty, never throws. The constructor path
/// override is the test seam (no real file touched).
/// </summary>
public sealed class PipelineProfileStore
{
    private readonly string _path;
    private readonly Dictionary<string, Dictionary<string, string?>> _profiles = new(StringComparer.OrdinalIgnoreCase);
    private string? _active;

    /// <summary>Raised after any mutation (save / delete / activate) so the shell refreshes its list.</summary>
    public event Action? Changed;

    public PipelineProfileStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "voxa-pipelines.json");
        Load();
    }

    public IReadOnlyList<string> Names =>
        _profiles.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The active profile name, or null if none is set (or it was deleted).</summary>
    public string? ActiveName => _active is not null && _profiles.ContainsKey(_active) ? _active : null;

    public bool TryGet(string name, out IReadOnlyDictionary<string, string?> pairs)
    {
        if (_profiles.TryGetValue(name, out var p)) { pairs = p; return true; }
        pairs = new Dictionary<string, string?>();
        return false;
    }

    /// <summary>The active profile's pairs, or false if none is active.</summary>
    public bool TryGetActive(out IReadOnlyDictionary<string, string?> pairs)
        => TryGet(ActiveName ?? "", out pairs);

    /// <summary>Create or overwrite a profile (and persist). Secret keys and empties are dropped.</summary>
    public void Save(string name, IReadOnlyDictionary<string, string?> pairs)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        _profiles[name.Trim()] = pairs
            .Where(kv => !IsSecret(kv.Key) && !string.IsNullOrEmpty(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        Persist();
    }

    public void Delete(string name)
    {
        if (!_profiles.Remove(name)) return;
        if (string.Equals(_active, name, StringComparison.OrdinalIgnoreCase)) _active = null;
        Persist();
    }

    public void SetActive(string? name)
    {
        _active = name is not null && _profiles.ContainsKey(name) ? name : null;
        Persist();
    }

    private static bool IsSecret(string key) => key.EndsWith(":ApiKey", StringComparison.OrdinalIgnoreCase);

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            if (JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(_path)) is { } snap)
            {
                if (snap.Profiles is not null)
                    foreach (var (name, pairs) in snap.Profiles)
                        if (!string.IsNullOrWhiteSpace(name) && pairs is not null)
                            _profiles[name] = new Dictionary<string, string?>(pairs, StringComparer.Ordinal);
                _active = snap.Active;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException) { /* defaults */ }
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(
                new Snapshot(ActiveName, _profiles), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException) { /* a failed profile write must never crash the app */ }
        Changed?.Invoke();
    }

    private sealed record Snapshot(string? Active, Dictionary<string, Dictionary<string, string?>> Profiles);
}
