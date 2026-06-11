using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Voxa.Speech;

/// <summary>
/// Shared model/binary resolver for the local speech tier (VLS-001 WS0). Resolution order:
/// cache hit → verified download (SHA-256, atomic rename) — unless <see cref="VoxaModelCacheOptions.Offline"/>,
/// in which case a miss throws with a copy-pasteable provisioning instruction. Explicit-path
/// overrides (<c>ModelPath</c> / <c>VoicePath</c> / …) are a descriptor concern and bypass the
/// cache entirely.
///
/// <para>
/// Contract split that keeps "never download at request time" and "validation has no side
/// effects" simultaneously true: descriptor <c>Validate</c> may only call <see cref="IsCached"/> /
/// <see cref="PathFor"/> (pure probes); <see cref="ResolveAsync"/> (which may download) runs from
/// the startup guard's warm-up or an engine's <c>StartAsync</c>.
/// </para>
/// </summary>
public sealed class VoxaModelCache
{
    // Fallback for hosts that don't pass an HttpClient (manual composition, tests). Infinite
    // client timeout — per-resolve cancellation below owns the deadline, and model downloads
    // legitimately run for many minutes.
    private static readonly Lazy<HttpClient> SharedHttp = new(
        () => new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Generous per-artifact ceiling — models are large, corp networks are slow.</summary>
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromMinutes(30);

    private readonly VoxaModelCacheOptions _options;
    private readonly HttpClient _http;
    private readonly ILogger _logger;

    public VoxaModelCache(VoxaModelCacheOptions options, HttpClient? http = null, ILogger? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _http = http ?? SharedHttp.Value;
        _logger = logger ?? NullLogger.Instance;
    }

    public VoxaModelCacheOptions Options => _options;

    /// <summary>
    /// The path <see cref="ResolveAsync"/> would return: the cached file itself, or for archive
    /// artifacts the extracted entry. Pure — no filesystem side effects.
    /// </summary>
    public string PathFor(VoxaModelArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return artifact.ArchiveEntry is null
            ? DownloadPathFor(artifact)
            : Path.Combine(ExtractDirFor(artifact), ToLocalRelativePath(artifact.ArchiveEntry, nameof(artifact.ArchiveEntry)));
    }

    /// <summary>Pure probe used by descriptor <c>Validate</c> — never downloads, never creates directories.</summary>
    public bool IsCached(VoxaModelArtifact artifact) => File.Exists(PathFor(artifact));

    /// <summary>
    /// Resolve the artifact to a local path, downloading and verifying on a cache miss. Safe to
    /// call concurrently from multiple threads or processes — a lock file serializes first-run
    /// downloads and losers return the winner's file.
    /// </summary>
    /// <exception cref="VoxaModelUnavailableException">
    /// Offline cache miss, hash mismatch, or download failure — message includes the remediation.
    /// </exception>
    public async Task<string> ResolveAsync(VoxaModelArtifact artifact, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        var finalPath = PathFor(artifact);
        if (File.Exists(finalPath)) return finalPath;

        if (_options.Offline)
            throw new VoxaModelUnavailableException(OfflineMissMessage(artifact, finalPath));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ResolveTimeout);
        var token = timeout.Token;

        var downloadPath = DownloadPathFor(artifact);
        Directory.CreateDirectory(Path.GetDirectoryName(downloadPath)!);

        var lockPath = downloadPath + ".lock";
        await using (await AcquireLockAsync(artifact, lockPath, token).ConfigureAwait(false))
        {
            // Re-probe under the lock: a concurrent resolver (this process or another) may have
            // completed while we waited.
            if (File.Exists(finalPath)) return finalPath;

            var partialPath = downloadPath + ".partial";
            try
            {
                await DownloadAndVerifyAsync(artifact, partialPath, token).ConfigureAwait(false);

                if (artifact.ArchiveEntry is null)
                {
                    File.Move(partialPath, downloadPath, overwrite: true);
                    if (artifact.Executable) MakeExecutable(downloadPath);
                }
                else
                {
                    await ExtractArchiveAsync(artifact, partialPath, token).ConfigureAwait(false);
                    File.Delete(partialPath); // extracted content is the cache entry; reclaim the disk
                    if (!File.Exists(finalPath))
                    {
                        throw new VoxaModelUnavailableException(
                            $"Archive artifact '{artifact.Id}' downloaded and extracted, but the expected entry " +
                            $"'{artifact.ArchiveEntry}' was not found at {finalPath}. The pinned catalog entry is wrong " +
                            "for this archive layout — update the Voxa package or set an explicit path override.");
                    }
                    if (artifact.Executable) MakeExecutable(finalPath);
                }
            }
            catch (Exception ex) when (ex is not VoxaModelUnavailableException and not OperationCanceledException)
            {
                TryDelete(partialPath);
                throw new VoxaModelUnavailableException(
                    $"Failed to download model artifact '{artifact.Id}' from {artifact.DownloadUrl}: {ex.Message} " +
                    $"Check network/proxy settings, or provision the file manually at {finalPath} " +
                    "and (for air-gapped hosts) set Voxa:Models:Offline to true.",
                    ex);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                TryDelete(partialPath);
                throw new VoxaModelUnavailableException(
                    $"Timed out downloading model artifact '{artifact.Id}' from {artifact.DownloadUrl} " +
                    $"after {ResolveTimeout.TotalMinutes:F0} minutes (~{SizeMb(artifact)} MB expected). " +
                    $"Provision the file manually at {finalPath} if the network can't sustain the download.");
            }
        }

        return finalPath;
    }

    // ── download + verify ───────────────────────────────────────────────────

    private async Task DownloadAndVerifyAsync(VoxaModelArtifact artifact, string partialPath, CancellationToken ct)
    {
        _logger.LogInformation(
            "Voxa model cache: downloading '{Id}' (~{SizeMb} MB) from {Url}",
            artifact.Id, SizeMb(artifact), artifact.DownloadUrl);

        using var response = await _http
            .GetAsync(artifact.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long? totalBytes = response.Content.Headers.ContentLength
                           ?? (artifact.SizeBytes > 0 ? artifact.SizeBytes : null);

        // Hash while streaming — no second read pass over a multi-hundred-MB file.
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long written = 0;
        int lastLoggedDecile = 0;

        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var destination = new FileStream(
            partialPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                sha.AppendData(buffer, 0, read);
                written += read;

                if (totalBytes is > 0)
                {
                    var decile = (int)(written * 10 / totalBytes.Value);
                    if (decile > lastLoggedDecile)
                    {
                        lastLoggedDecile = decile;
                        _logger.LogInformation(
                            "Voxa model cache: '{Id}' {Percent}% ({WrittenMb}/{TotalMb} MB)",
                            artifact.Id, decile * 10, written / (1024 * 1024), totalBytes.Value / (1024 * 1024));
                    }
                }
            }
        }

        var actual = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        if (!string.Equals(actual, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(partialPath);
            throw new VoxaModelUnavailableException(
                $"Downloaded artifact '{artifact.Id}' failed SHA-256 verification and the partial file was deleted." +
                $"{Environment.NewLine}  expected: {artifact.Sha256.ToLowerInvariant()}" +
                $"{Environment.NewLine}  actual:   {actual}" +
                $"{Environment.NewLine}This usually means the upstream file changed or the download was corrupted. " +
                "Retry; if it persists, the pinned catalog is stale — update the Voxa package or use an explicit path override.");
        }

        _logger.LogInformation("Voxa model cache: '{Id}' downloaded and verified ({WrittenMb} MB)",
            artifact.Id, written / (1024 * 1024));
    }

    private async Task ExtractArchiveAsync(VoxaModelArtifact artifact, string archivePath, CancellationToken ct)
    {
        var extractDir = ExtractDirFor(artifact);
        var tempDir = extractDir + ".tmp-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(tempDir);
        try
        {
            var name = artifact.DownloadUrl.AbsolutePath;
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.ExtractToDirectory(archivePath, tempDir);
            }
            else if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
                  || name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
            {
                await using var file = File.OpenRead(archivePath);
                await using var gzip = new GZipStream(file, CompressionMode.Decompress);
                await TarFile.ExtractToDirectoryAsync(gzip, tempDir, overwriteFiles: false, ct).ConfigureAwait(false);
            }
            else
            {
                throw new VoxaModelUnavailableException(
                    $"Artifact '{artifact.Id}' declares ArchiveEntry '{artifact.ArchiveEntry}' but its URL " +
                    $"({artifact.DownloadUrl}) is neither .zip nor .tar.gz — the catalog entry is malformed.");
            }

            // Atomic publish: extraction either fully lands at extractDir or not at all.
            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true);
            Directory.Move(tempDir, extractDir);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
            }
        }
    }

    // ── cross-process lock ──────────────────────────────────────────────────

    /// <summary>
    /// Exclusive-create lock file serializing concurrent first-run downloads (parallel test
    /// hosts, multi-instance startup). DeleteOnClose reclaims it even on most failures; a hard
    /// process kill on Unix can leave a stale lock, which the timeout message tells the user to
    /// delete (Windows removes the handle-backed file automatically).
    /// </summary>
    private async Task<FileStream> AcquireLockAsync(VoxaModelArtifact artifact, string lockPath, CancellationToken ct)
    {
        var loggedWait = false;
        var deadline = DateTime.UtcNow + ResolveTimeout;
        while (true)
        {
            try
            {
                return new FileStream(
                    lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    bufferSize: 1, FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                if (!loggedWait)
                {
                    loggedWait = true;
                    _logger.LogInformation(
                        "Voxa model cache: waiting for a concurrent download of '{Id}' (lock: {LockPath})",
                        artifact.Id, lockPath);
                }
                if (DateTime.UtcNow >= deadline)
                {
                    throw new VoxaModelUnavailableException(
                        $"Timed out waiting for the download lock on '{artifact.Id}' ({lockPath}). " +
                        "If no other Voxa process is downloading, a previous run died and left the lock behind — " +
                        "delete the .lock file and retry.");
                }
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
        }
    }

    // ── paths + helpers ─────────────────────────────────────────────────────

    private string DownloadPathFor(VoxaModelArtifact artifact)
        => Path.Combine(_options.CacheRoot, ToLocalRelativePath(artifact.Id, nameof(artifact.Id)));

    private string ExtractDirFor(VoxaModelArtifact artifact)
        => DownloadPathFor(artifact) + ".extracted";

    /// <summary>Convert a forward-slash catalog path to a local relative path, rejecting escapes.</summary>
    private static string ToLocalRelativePath(string catalogPath, string paramName)
    {
        var segments = catalogPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s is "." or ".." || Path.IsPathRooted(s)))
            throw new ArgumentException($"Invalid catalog path '{catalogPath}'.", paramName);
        return Path.Combine(segments);
    }

    private string OfflineMissMessage(VoxaModelArtifact artifact, string finalPath)
    {
        var archiveNote = artifact.ArchiveEntry is null
            ? $"and place it at:{Environment.NewLine}  {finalPath}"
            : $"then extract the archive to:{Environment.NewLine}  {ExtractDirFor(artifact)}{Environment.NewLine}" +
              $"(resolution expects the entry '{artifact.ArchiveEntry}' inside it)";

        return
            $"Model artifact '{artifact.Id}' is not in the local cache and Voxa:Models:Offline is true." +
            $"{Environment.NewLine}Expected at: {finalPath}" +
            $"{Environment.NewLine}To provision it out-of-band, download:" +
            $"{Environment.NewLine}  {artifact.DownloadUrl}" +
            $"{Environment.NewLine}verify its SHA-256 is {artifact.Sha256.ToLowerInvariant()} (~{SizeMb(artifact)} MB), {archiveNote}" +
            $"{Environment.NewLine}Or set Voxa:Models:Offline to false to allow a first-run download.";
    }

    private static long SizeMb(VoxaModelArtifact artifact)
        => Math.Max(1, artifact.SizeBytes / (1024 * 1024));

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }
}
