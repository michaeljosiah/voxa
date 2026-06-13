namespace Voxa.Studio.Services;

/// <summary>
/// In-memory <see cref="ISecretsStore"/> for tests and non-Windows hosts (VST-003 WS1). No disk,
/// no DPAPI. <see cref="Save"/> snapshots the working copy; <see cref="Reload"/> restores it — so
/// the Cancel semantics tests exercise the same code path the DPAPI store does.
/// </summary>
public sealed class MemorySecretsStore : ISecretsStore
{
    private Dictionary<string, string> _live;
    private Dictionary<string, string> _saved;

    /// <param name="seed">Optional initial values, treated as the saved baseline.</param>
    public MemorySecretsStore(IReadOnlyDictionary<string, string>? seed = null)
    {
        _saved = seed is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(seed, StringComparer.Ordinal);
        _live = new Dictionary<string, string>(_saved, StringComparer.Ordinal);
    }

    public string? Get(string key) => _live.TryGetValue(key, out var v) ? v : null;

    public void Set(string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) _live.Remove(key);
        else _live[key] = value;
    }

    public IReadOnlyCollection<string> Keys => _live.Keys;

    public void Save() => _saved = new Dictionary<string, string>(_live, StringComparer.Ordinal);

    public void Reload() => _live = new Dictionary<string, string>(_saved, StringComparer.Ordinal);
}
