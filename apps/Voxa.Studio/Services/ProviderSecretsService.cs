namespace Voxa.Studio.Services;

/// <summary>The credential state of a provider identity, for the Settings status dot (VST-003 WS3).</summary>
public enum ProviderStatus
{
    /// <summary>Local provider — always available, no credentials. Grey dot.</summary>
    Local,

    /// <summary>All required secret fields are present. Green dot.</summary>
    Configured,

    /// <summary>Activated but a required secret is missing. Amber dot.</summary>
    KeyMissing,
}

/// <summary>
/// The facade the Settings dialog and the Config filter talk to (VST-003 WS3): activation + secrets
/// over the two stores, plus <see cref="BuildConfigPairs"/> which produces the flat <c>Voxa:X:Y</c>
/// pairs for <see cref="StudioServices"/>'s secrets configuration layer.
///
/// <para>
/// Mutations land in the stores' in-memory working copies; nothing reaches disk until <see cref="Save"/>.
/// The Settings dialog edits a working copy in its view-models and only calls <see cref="Activate"/> /
/// <see cref="SetSecret"/> / <see cref="Save"/> on a deliberate Save — so a Cancel needs no rollback.
/// <see cref="Discard"/> is provided for completeness (reloads both stores from disk).
/// </para>
/// </summary>
public sealed class ProviderSecretsService
{
    private readonly ISecretsStore _secrets;
    private readonly ProviderActivationStore _activationStore;
    private readonly List<ProviderActivation> _activations;

    public ProviderSecretsService(ISecretsStore secrets, ProviderActivationStore activationStore)
    {
        _secrets = secrets;
        _activationStore = activationStore;
        _activations = _activationStore.Load().ToList();
    }

    /// <summary>
    /// The identities to show in Settings and offer in Config: every local provider (always) plus the
    /// cloud providers the user has activated, in catalog order.
    /// </summary>
    public IReadOnlyList<ProviderManifest> Activated =>
        ProviderManifestCatalog.All.Where(IsActivated).ToList();

    public bool IsActivated(ProviderManifest manifest) =>
        manifest.IsLocal || IsActivatedName(manifest.Name);

    public ProviderStatus StatusOf(ProviderManifest manifest)
    {
        if (manifest.IsLocal) return ProviderStatus.Local;
        var allPresent = manifest.Fields
            .Where(f => f.IsSecret)
            .All(f => !string.IsNullOrEmpty(GetSecret(manifest.Name, f.Name)));
        return allPresent ? ProviderStatus.Configured : ProviderStatus.KeyMissing;
    }

    public string? GetSecret(string providerName, string fieldName) =>
        _secrets.Get(StoreKey(providerName, fieldName));

    public void SetSecret(string providerName, string fieldName, string? value) =>
        _secrets.Set(StoreKey(providerName, fieldName), value);

    /// <summary>Switch a cloud provider on (idempotent). Throws for local providers (always on).</summary>
    public void Activate(string providerName)
    {
        var manifest = Require(providerName);
        if (manifest.IsLocal)
            throw new InvalidOperationException($"'{providerName}' is a local provider and is always active.");
        if (!IsActivatedName(providerName))
            _activations.Add(new ProviderActivation(manifest.Name, DateTimeOffset.UtcNow));
    }

    /// <summary>Switch a cloud provider off and clear its stored secrets. Throws for local providers.</summary>
    public void Deactivate(string providerName)
    {
        var manifest = Require(providerName);
        if (manifest.IsLocal)
            throw new InvalidOperationException($"'{providerName}' is a local provider and cannot be deactivated.");
        _activations.RemoveAll(a => NameEquals(a.Name, providerName));
        foreach (var field in manifest.Fields)
            _secrets.Set(StoreKey(providerName, field.Name), null);
    }

    /// <summary>Persist both stores to disk.</summary>
    public void Save()
    {
        _secrets.Save();
        _activationStore.Save(_activations);
    }

    /// <summary>Drop in-memory edits, re-reading both stores from disk.</summary>
    public void Discard()
    {
        _secrets.Reload();
        _activations.Clear();
        _activations.AddRange(_activationStore.Load());
    }

    /// <summary>
    /// Flat <c>Voxa:X:Y</c> pairs for the secrets configuration layer — every non-empty stored field
    /// value mapped through its <see cref="ProviderFieldDescriptor.ConfigKey"/>. Store-driven, so a key
    /// the user has entered flows to the live container; <see cref="Deactivate"/> clears the store, which
    /// removes the key here too. Never emits a file path or the store's location.
    /// </summary>
    public Dictionary<string, string?> BuildConfigPairs()
    {
        var pairs = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var manifest in ProviderManifestCatalog.All.Where(m => !m.IsLocal))
        foreach (var field in manifest.Fields)
        {
            var value = GetSecret(manifest.Name, field.Name);
            if (!string.IsNullOrEmpty(value)) pairs[field.ConfigKey] = value;
        }
        return pairs;
    }

    private static string StoreKey(string providerName, string fieldName) => $"{providerName}:{fieldName}";

    private bool IsActivatedName(string name) => _activations.Any(a => NameEquals(a.Name, name));

    private static bool NameEquals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static ProviderManifest Require(string name) =>
        ProviderManifestCatalog.Find(name)
        ?? throw new InvalidOperationException($"Unknown provider '{name}'.");
}
