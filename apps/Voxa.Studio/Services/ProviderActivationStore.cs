using System.Text.Json;
using System.Text.Json.Serialization;

namespace Voxa.Studio.Services;

/// <summary>Which provider the user has switched on, and when (VST-003 WS1).</summary>
public sealed record ProviderActivation(string Name, DateTimeOffset ActivatedAt);

/// <summary>
/// The on-disk list of activated providers (VST-003 WS1): a single <c>~/voxa-activations.json</c>
/// array — non-sensitive (no keys, just names), so plain JSON, no encryption. Mirrors
/// <see cref="VoiceStore"/>'s discipline: a constructor override as the test seam (no config key),
/// and a corrupt/hand-edited file is treated as empty rather than fatal.
/// </summary>
public sealed class ProviderActivationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    public ProviderActivationStore(string? path = null) =>
        _path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "voxa-activations.json");

    public string FilePath => _path;

    /// <summary>The activated providers. An unreadable file yields an empty list, never an exception.</summary>
    public IReadOnlyList<ProviderActivation> Load()
    {
        if (!File.Exists(_path)) return [];
        try
        {
            var list = JsonSerializer.Deserialize<List<ProviderActivation>>(File.ReadAllText(_path), JsonOptions);
            return list ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return [];
        }
    }

    public void Save(IEnumerable<ProviderActivation> activations)
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(activations.ToList(), JsonOptions));
    }
}
