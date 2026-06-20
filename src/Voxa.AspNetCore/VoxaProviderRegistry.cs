using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Voxa.Speech;
using Voxa.Speech.Voices;

namespace Voxa.AspNetCore;

/// <summary>
/// Registry of STT, TTS, and VAD provider descriptors. Populated during AddVoxa() and used
/// by DefaultVoicePipelineComposer to materialise processors from config-driven names.
/// Case-insensitive lookups; last registration wins (enables test overrides).
/// </summary>
public sealed class VoxaProviderRegistry
{
    private readonly Dictionary<string, VoxaSttDescriptor> _stt =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VoxaTtsDescriptor> _tts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VoxaVadDescriptor> _vad =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VoxaAecDescriptor> _aec =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VoxaEnhancerDescriptor> _enhancer =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> SttNames => _stt.Keys;
    public IReadOnlyCollection<string> TtsNames => _tts.Keys;
    public IReadOnlyCollection<string> VadNames => _vad.Keys;
    public IReadOnlyCollection<string> AecNames => _aec.Keys;
    public IReadOnlyCollection<string> EnhancerNames => _enhancer.Keys;

    internal void Add(VoxaSttDescriptor descriptor, ILogger? logger = null)
    {
        if (_stt.ContainsKey(descriptor.Name))
            logger?.LogDebug("VoxaProviderRegistry: STT '{Name}' overwritten.", descriptor.Name);
        _stt[descriptor.Name] = descriptor;
    }

    internal void Add(VoxaTtsDescriptor descriptor, ILogger? logger = null)
    {
        if (_tts.ContainsKey(descriptor.Name))
            logger?.LogDebug("VoxaProviderRegistry: TTS '{Name}' overwritten.", descriptor.Name);
        _tts[descriptor.Name] = descriptor;
    }

    internal void Add(VoxaVadDescriptor descriptor, ILogger? logger = null)
    {
        if (_vad.ContainsKey(descriptor.Name))
            logger?.LogDebug("VoxaProviderRegistry: VAD '{Name}' overwritten.", descriptor.Name);
        _vad[descriptor.Name] = descriptor;
    }

    public bool TryGetStt(string name, out VoxaSttDescriptor descriptor)
        => _stt.TryGetValue(name, out descriptor!);

    public bool TryGetTts(string name, out VoxaTtsDescriptor descriptor)
        => _tts.TryGetValue(name, out descriptor!);

    internal void Add(VoxaAecDescriptor descriptor, ILogger? logger = null)
    {
        if (_aec.ContainsKey(descriptor.Name))
            logger?.LogDebug("VoxaProviderRegistry: AEC '{Name}' overwritten.", descriptor.Name);
        _aec[descriptor.Name] = descriptor;
    }

    public bool TryGetVad(string name, out VoxaVadDescriptor descriptor)
        => _vad.TryGetValue(name, out descriptor!);

    internal void Add(VoxaEnhancerDescriptor descriptor, ILogger? logger = null)
    {
        if (_enhancer.ContainsKey(descriptor.Name))
            logger?.LogDebug("VoxaProviderRegistry: Enhancer '{Name}' overwritten.", descriptor.Name);
        _enhancer[descriptor.Name] = descriptor;
    }

    public bool TryGetAec(string name, out VoxaAecDescriptor descriptor)
        => _aec.TryGetValue(name, out descriptor!);

    public bool TryGetEnhancer(string name, out VoxaEnhancerDescriptor descriptor)
        => _enhancer.TryGetValue(name, out descriptor!);

    /// <summary>
    /// Resolve the live voice-catalog capability for a named TTS provider, if it offers one
    /// (VVL-001 WS0). Sugar over <see cref="TryGetTts"/> + <c>descriptor.ResolveCatalog</c>.
    /// </summary>
    /// <param name="ttsName">The TTS provider name (e.g. "ElevenLabs").</param>
    /// <param name="services">DI provider passed to the resolver (for an <c>HttpClient</c> etc.).</param>
    /// <param name="voxaRoot">
    /// The captured <c>"Voxa"</c> configuration section. The caller supplies it (the composer holds
    /// one; Studio passes <c>services.Configuration.GetSection("Voxa")</c>) because the registry must
    /// never service-locate <see cref="IConfiguration"/> — plain-<c>ServiceCollection</c> hosts have none.
    /// </param>
    /// <param name="provider">The resolved catalog capability when the method returns true.</param>
    public bool TryGetVoiceCatalog(
        string ttsName, IServiceProvider services, IConfigurationSection voxaRoot,
        out IVoiceCatalogProvider provider)
    {
        if (_tts.TryGetValue(ttsName, out var descriptor) && descriptor.ResolveCatalog is { } resolve)
        {
            provider = resolve(services, voxaRoot);
            return true;
        }
        provider = null!;
        return false;
    }

    /// <summary>
    /// Resolve the voice-cloning capability for a named TTS provider, if it offers one (VVL-001 WS0).
    /// The host owns the consent gate — this only constructs the transport. See
    /// <see cref="TryGetVoiceCatalog"/> for why <paramref name="voxaRoot"/> is caller-supplied.
    /// </summary>
    public bool TryGetVoiceCloner(
        string ttsName, IServiceProvider services, IConfigurationSection voxaRoot,
        out IVoiceCloneProvider provider)
    {
        if (_tts.TryGetValue(ttsName, out var descriptor) && descriptor.ResolveCloner is { } resolve)
        {
            provider = resolve(services, voxaRoot);
            return true;
        }
        provider = null!;
        return false;
    }
}
