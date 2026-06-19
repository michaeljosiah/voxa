using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Voxa.Speech.Voices;

namespace Voxa.Speech.Sidecar;

/// <summary>
/// Local voice cloning for the sidecar TTS provider (VVL-002): persists a reference clip and returns a
/// <see cref="ProviderVoice"/> whose <c>Id</c> is that clip's path — which <see cref="SidecarTtsEngine"/>
/// then passes to the sidecar as the speaker reference (zero-shot cloning, e.g. XTTS-v2/OpenVoice). No
/// network and no account; the consent gate lives in the host (Studio). Fills VVL-001's deferred local
/// cloning slot. Resolved via <c>VoxaTtsDescriptor.ResolveCloner</c>.
/// </summary>
internal sealed class SidecarVoiceCloneProvider : IVoiceCloneProvider
{
    private readonly string _voicesDirectory;
    private readonly ILogger _logger;

    public SidecarVoiceCloneProvider(string voicesDirectory, ILogger? logger = null)
    {
        _voicesDirectory = voicesDirectory ?? throw new ArgumentNullException(nameof(voicesDirectory));
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<ProviderVoice> CreateVoiceAsync(VoiceCloneRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Samples is not { Count: > 0 })
            throw new VoiceProviderException("Sidecar cloning needs at least one reference clip.");

        Directory.CreateDirectory(_voicesDirectory);
        var path = Path.Combine(_voicesDirectory, $"{Sanitize(request.Name)}-{Guid.NewGuid():N}.wav");

        // XTTS-v2/OpenVoice are zero-shot — the reference clip IS the voice. Persist the primary sample
        // and hand back its path as the synthesis handle (the engine passes it as the speaker reference).
        await File.WriteAllBytesAsync(path, request.Samples[0].Data.ToArray(), ct).ConfigureAwait(false);

        _logger.LogInformation("Voxa sidecar: cloned voice '{Name}' -> {Path}", request.Name, path);
        return new ProviderVoice(
            Id: path,
            DisplayName: request.Name,
            ProviderName: SidecarDescriptors.Tts.Name,
            Kind: VoiceKind.Cloned,
            Language: request.Language,
            Description: request.Description);
    }

    public Task DeleteVoiceAsync(string voiceId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(voiceId);

        // voiceId is a caller-supplied path — only ever delete inside the managed voices directory.
        var target = Path.GetFullPath(voiceId);
        var root = Path.GetFullPath(_voicesDirectory) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(root, StringComparison.Ordinal))
            throw new VoiceProviderException($"Refusing to delete '{voiceId}': it is outside the sidecar voices directory.");

        if (File.Exists(target)) File.Delete(target);
        return Task.CompletedTask;
    }

    private static string Sanitize(string name)
    {
        var cleaned = new string((name ?? string.Empty)
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "voice" : cleaned;
    }
}
