using Microsoft.Extensions.Logging;
using Voxa.Speech;

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

    public IReadOnlyCollection<string> SttNames => _stt.Keys;
    public IReadOnlyCollection<string> TtsNames => _tts.Keys;
    public IReadOnlyCollection<string> VadNames => _vad.Keys;

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

    public bool TryGetVad(string name, out VoxaVadDescriptor descriptor)
        => _vad.TryGetValue(name, out descriptor!);
}
