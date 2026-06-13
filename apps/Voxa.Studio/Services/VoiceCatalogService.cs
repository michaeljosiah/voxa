using Voxa.Speech.Kokoro;
using Voxa.Speech.Piper;
using Voxa.Speech.Voices;

namespace Voxa.Studio.Services;

/// <summary>How a library row relates to its provider's live truth (VVL-001 §4.2).</summary>
public enum VoiceState
{
    /// <summary>A saved profile whose voice is confirmed present in the provider's live list.</summary>
    Live,

    /// <summary>A saved profile whose voice is no longer in the provider's list (deleted/key changed).</summary>
    Stale,

    /// <summary>A live provider voice with no saved profile yet (made elsewhere, or a stock voice).</summary>
    Discovered,

    /// <summary>A compiled-in local catalog voice (Piper/Kokoro) — no live list, cannot drift.</summary>
    LocalCatalog,
}

/// <summary>One row in the library/picker: the voice, how it reconciles, and its profile if saved.</summary>
public sealed record LibraryVoice(ProviderVoice Voice, VoiceState State, VoiceProfile? Profile = null);

/// <summary>
/// The voices a single TTS provider offers right now, plus why the list might be empty — the
/// status strip (WS5) renders <see cref="MissingKey"/>/<see cref="Error"/> as a per-provider chip.
/// </summary>
public sealed record ProviderVoiceSet(
    string Provider,
    IReadOnlyList<LibraryVoice> Voices,
    bool MissingKey = false,
    string? Error = null);

/// <summary>
/// The picker's and library's single source of truth (VVL-001 WS4): for each TTS provider it merges
/// the compiled-in catalog (Piper/Kokoro), the provider's live <c>ListVoicesAsync</c>, and the saved
/// <see cref="VoiceProfile"/>s, reconciling each into a <see cref="VoiceState"/>. A live list is
/// short-cached (default 60 s) and never persisted as authoritative.
///
/// <para>Takes <see cref="StudioServices"/> rather than a fixed registry/config, because Config
/// Apply rebuilds the container — the registry and the captured "Voxa" section are resolved per call
/// so the service follows the swap (the caller invalidates the cache via <see cref="Invalidate"/>).</para>
/// </summary>
public sealed class VoiceCatalogService
{
    private readonly StudioServices _services;
    private readonly VoiceStore _store;
    private readonly Dictionary<string, (DateTime At, IReadOnlyList<ProviderVoice> Voices)> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _cacheLock = new();

    public VoiceCatalogService(StudioServices services, VoiceStore store)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>How long a live provider list is reused before a refetch. A Studio constant; a test seam.</summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Test seam: resolve a provider's live catalog without the registry (the <c>SessionFactoryOverride</c>
    /// precedent). Returns null to fall through to the real registry. Lets headless tests feed
    /// controlled voices and key-required failures.
    /// </summary>
    internal Func<string, IVoiceCatalogProvider?>? CatalogOverride { get; set; }

    /// <summary>Drop cached provider lists — called after a Config Apply (registry/keys may have changed).</summary>
    public void Invalidate()
    {
        lock (_cacheLock) _cache.Clear();
    }

    /// <summary>The merged, reconciled voices for one TTS provider.</summary>
    public async Task<ProviderVoiceSet> ForProviderAsync(string ttsName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ttsName))
            return new ProviderVoiceSet(ttsName ?? "", []);

        var profiles = _store.Load()
            .Where(p => string.Equals(p.ProviderName, ttsName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Live-catalog providers (ElevenLabs, Mistral, VoiceClone) reconcile against ListVoicesAsync.
        var catalog = CatalogOverride?.Invoke(ttsName);
        if (catalog is not null
            || _services.Registry.TryGetVoiceCatalog(ttsName, _services.Provider, VoxaRoot(), out catalog))
        {
            IReadOnlyList<ProviderVoice> live;
            try
            {
                live = await GetLiveAsync(ttsName, catalog, ct).ConfigureAwait(false);
            }
            catch (VoiceProviderException ex)
            {
                // No key / provider error: still surface saved profiles (as Stale — unverifiable).
                var stale = profiles
                    .Select(p => new LibraryVoice(ToProviderVoice(p), VoiceState.Stale, p))
                    .ToList();
                return new ProviderVoiceSet(ttsName, stale, ex.MissingApiKey, ex.Message);
            }

            return new ProviderVoiceSet(ttsName, Reconcile(live, profiles));
        }

        // Compiled-in local catalogs — listed as LocalCatalog, plus any saved profiles for them.
        var localCatalog = LocalCatalogVoices(ttsName);
        if (localCatalog.Count > 0 || profiles.Count > 0)
            return new ProviderVoiceSet(ttsName, Reconcile(localCatalog, profiles, isLocalCatalog: true));

        return new ProviderVoiceSet(ttsName, []);
    }

    /// <summary>Every TTS provider's set — the library grid (WS5) and the status strip.</summary>
    public async Task<IReadOnlyList<ProviderVoiceSet>> AllAsync(CancellationToken ct)
    {
        var sets = new List<ProviderVoiceSet>();
        foreach (var name in _services.Registry.TtsNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            sets.Add(await ForProviderAsync(name, ct).ConfigureAwait(false));
        return sets;
    }

    private async Task<IReadOnlyList<ProviderVoice>> GetLiveAsync(
        string ttsName, IVoiceCatalogProvider catalog, CancellationToken ct)
    {
        lock (_cacheLock)
        {
            if (CacheDuration > TimeSpan.Zero
                && _cache.TryGetValue(ttsName, out var hit)
                && DateTime.UtcNow - hit.At < CacheDuration)
            {
                return hit.Voices;
            }
        }

        var voices = await catalog.ListVoicesAsync(ct).ConfigureAwait(false);

        lock (_cacheLock) _cache[ttsName] = (DateTime.UtcNow, voices);
        return voices;
    }

    // Reconcile a provider's voices against saved profiles into tagged rows (§4.2).
    private static IReadOnlyList<LibraryVoice> Reconcile(
        IReadOnlyList<ProviderVoice> voices, IReadOnlyList<VoiceProfile> profiles, bool isLocalCatalog = false)
    {
        var byId = profiles.ToDictionary(p => p.ProviderVoiceId, p => p, StringComparer.OrdinalIgnoreCase);
        var rows = new List<LibraryVoice>(voices.Count + profiles.Count);
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var v in voices)
        {
            if (byId.TryGetValue(v.Id, out var profile))
            {
                matched.Add(v.Id);
                rows.Add(new LibraryVoice(v, VoiceState.Live, profile));
            }
            else
            {
                rows.Add(new LibraryVoice(v, isLocalCatalog ? VoiceState.LocalCatalog : VoiceState.Discovered));
            }
        }

        // Saved profiles the provider no longer lists are stale — shown, never silently usable.
        foreach (var p in profiles)
            if (!matched.Contains(p.ProviderVoiceId))
                rows.Add(new LibraryVoice(ToProviderVoice(p), VoiceState.Stale, p));

        return rows;
    }

    // The compiled-in catalogs Studio already surfaces in Config. Cloud providers return empty.
    private static IReadOnlyList<ProviderVoice> LocalCatalogVoices(string ttsName) => ttsName.ToLowerInvariant() switch
    {
        "piper"  => PiperVoiceCatalog.KnownVoices.Select(v => Local(v, "Piper")).ToList(),
        "kokoro" => KokoroCatalog.KnownVoices.Select(v => Local(v, "Kokoro")).ToList(),
        _ => [],
    };

    private static ProviderVoice Local(string id, string provider)
        => new(id, id, provider, VoiceKind.Standard);

    private static ProviderVoice ToProviderVoice(VoiceProfile p)
        => new(p.ProviderVoiceId, p.DisplayName, p.ProviderName, p.Kind, p.Language);

    private Microsoft.Extensions.Configuration.IConfigurationSection VoxaRoot()
        => _services.Configuration.GetSection("Voxa");
}
