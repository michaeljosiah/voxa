namespace Voxa.Speech;

/// <summary>
/// One entry in a local-speech provider's pinned artifact catalog (VLS-001 WS0): a model file,
/// voice file, or tool binary that <see cref="VoxaModelCache"/> can resolve. URLs and hashes are
/// compiled-in constants per package version — never floating references — so an upstream
/// re-upload or tampering fails the hash check loudly instead of changing behavior silently.
/// </summary>
/// <param name="Id">
/// Stable identifier, also the cache-relative path (forward slashes), e.g.
/// <c>"whisper/ggml-tiny.en.bin"</c>.
/// </param>
/// <param name="DownloadUrl">Pinned download location.</param>
/// <param name="Sha256">Expected SHA-256 of the downloaded file, lowercase hex.</param>
/// <param name="SizeBytes">Approximate size, used for progress reporting and error messages.</param>
public sealed record VoxaModelArtifact(
    string Id,
    Uri DownloadUrl,
    string Sha256,
    long SizeBytes)
{
    /// <summary>
    /// When the artifact is an archive (<c>.zip</c> / <c>.tar.gz</c>), the path of the entry that
    /// resolution should return, relative to the archive root (forward slashes). The whole archive
    /// is extracted next to the download (tool binaries usually need their sibling data files);
    /// <see cref="VoxaModelCache.ResolveAsync"/> returns the extracted entry's full path.
    /// Null = the artifact is a plain file.
    /// </summary>
    public string? ArchiveEntry { get; init; }

    /// <summary>
    /// Mark the resolved file executable on Unix (piper / espeak-ng binaries). No-op on Windows.
    /// </summary>
    public bool Executable { get; init; }
}
