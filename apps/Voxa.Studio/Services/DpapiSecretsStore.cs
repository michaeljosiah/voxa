using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Voxa.Studio.Services;

/// <summary>
/// Windows DPAPI-backed <see cref="ISecretsStore"/> (VST-003 WS1). The working copy is serialised to
/// JSON, protected with <see cref="ProtectedData"/> at <see cref="DataProtectionScope.CurrentUser"/>
/// scope — ciphertext tied to the Windows user account, unreadable by other users or machines — and
/// written to <c>~/voxa-secrets.dpapi</c>. The file never leaves the box and is never part of any
/// export or bundle (the Settings dialog reads it only into the in-memory config layer).
///
/// <para>
/// A file that fails to decrypt (a different user's blob after a profile copy, a truncated write) is
/// treated as empty with a warning and is NOT overwritten until the user explicitly saves — losing a
/// re-typed key beats clobbering a recoverable one.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretsStore : ISecretsStore
{
    private readonly string _path;
    private Dictionary<string, string> _live;

    public DpapiSecretsStore(string path)
    {
        _path = path;
        _live = LoadFromDisk(path);
    }

    public string? Get(string key) => _live.TryGetValue(key, out var v) ? v : null;

    public void Set(string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) _live.Remove(key);
        else _live[key] = value;
    }

    public IReadOnlyCollection<string> Keys => _live.Keys;

    public void Save()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(_live);
        var cipher = ProtectedData.Protect(json, optionalEntropy: null, DataProtectionScope.CurrentUser);

        // temp + move so a crash mid-write can't leave a half-encrypted, undecryptable file.
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, cipher);
        File.Move(tmp, _path, overwrite: true);

        Array.Clear(json); // don't leave the plaintext JSON lingering on the heap longer than needed
    }

    public void Reload() => _live = LoadFromDisk(_path);

    private static Dictionary<string, string> LoadFromDisk(string path)
    {
        if (!File.Exists(path)) return new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var cipher = File.ReadAllBytes(path);
            var json = ProtectedData.Unprotect(cipher, optionalEntropy: null, DataProtectionScope.CurrentUser);
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(Encoding.UTF8.GetString(json));
            return map is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(map, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
        {
            // Unreadable blob → start empty; do not overwrite until the user saves.
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}
