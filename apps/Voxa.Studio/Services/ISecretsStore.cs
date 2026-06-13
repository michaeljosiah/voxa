namespace Voxa.Studio.Services;

/// <summary>
/// Per-key credential storage for the Settings dialog (VST-003 WS1). A narrow keyed-string map
/// with an explicit <see cref="Save"/> gate: callers mutate freely in memory, then choose when the
/// values hit disk (encrypted, on the DPAPI backend). <see cref="Reload"/> drops in-memory edits
/// back to the last saved snapshot — the backing for a dialog "Cancel".
///
/// <para>
/// Keys are namespaced by manifest identity + field, e.g. <c>"OpenAI:ApiKey"</c>,
/// <c>"Azure:SubscriptionKey"</c>, <c>"Azure:Region"</c>. The store holds non-secret field values
/// too (Azure's region): a field's secrecy governs UI masking, not whether it is stored. Nothing
/// here knows about <c>Voxa:*</c> config keys — <see cref="ProviderSecretsService"/> maps them.
/// </para>
/// </summary>
public interface ISecretsStore
{
    /// <summary>The stored value for a key, or null if unset.</summary>
    string? Get(string key);

    /// <summary>Set (or, with a null/empty value, remove) a key in the in-memory working copy.</summary>
    void Set(string key, string? value);

    /// <summary>The keys currently present in the working copy.</summary>
    IReadOnlyCollection<string> Keys { get; }

    /// <summary>Persist the working copy (encrypted on the DPAPI backend).</summary>
    void Save();

    /// <summary>Discard in-memory edits and re-read the last saved state.</summary>
    void Reload();
}
