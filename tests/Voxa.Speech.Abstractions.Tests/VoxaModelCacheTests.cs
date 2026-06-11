using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Voxa.Speech;

namespace Voxa.Speech.Abstractions.Tests;

/// <summary>
/// VLS-001 WS0 unit coverage for <see cref="VoxaModelCache"/>. No real network anywhere — every
/// download goes through a counting fake handler.
/// </summary>
public class VoxaModelCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "voxa-cache-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private static string Sha256Of(byte[] payload)
        => Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    private static VoxaModelArtifact ArtifactFor(
        byte[] payload, string id = "test/model.bin", string? sha = null, string? archiveEntry = null)
        => new(
            Id: id,
            DownloadUrl: new Uri($"https://models.example.test/{id}"),
            Sha256: sha ?? Sha256Of(payload),
            SizeBytes: payload.Length)
        { ArchiveEntry = archiveEntry };

    /// <summary>Serves a fixed payload for every request; counts calls; optional per-call delay.</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly byte[] _payload;
        private readonly TimeSpan _delay;
        private int _calls;

        public CountingHandler(byte[] payload, TimeSpan delay = default)
        {
            _payload = payload;
            _delay = delay;
        }

        public int Calls => Volatile.Read(ref _calls);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            if (_delay > TimeSpan.Zero) await Task.Delay(_delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_payload) };
        }
    }

    private VoxaModelCache CacheWith(CountingHandler handler, bool offline = false)
        => new(new VoxaModelCacheOptions(_root, offline), new HttpClient(handler));

    // ── happy path ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Resolve_Downloads_Verifies_Caches_And_Reuses()
    {
        var payload = "hello model"u8.ToArray();
        var handler = new CountingHandler(payload);
        var cache = CacheWith(handler);
        var artifact = ArtifactFor(payload);

        Assert.False(cache.IsCached(artifact));

        var path = await cache.ResolveAsync(artifact);

        Assert.True(File.Exists(path));
        Assert.Equal(payload, await File.ReadAllBytesAsync(path));
        Assert.True(cache.IsCached(artifact));
        Assert.Equal(1, handler.Calls);
        Assert.False(File.Exists(path + ".partial"));
        Assert.False(File.Exists(path + ".lock"));

        // Second resolve is a pure cache hit.
        var again = await cache.ResolveAsync(artifact);
        Assert.Equal(path, again);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Concurrent_Resolves_Download_Once()
    {
        var payload = new byte[64 * 1024];
        new Random(7).NextBytes(payload);
        // Delay forces real overlap so the loser actually waits on the lock file.
        var handler = new CountingHandler(payload, delay: TimeSpan.FromMilliseconds(300));
        var cache = CacheWith(handler);
        var artifact = ArtifactFor(payload, id: "test/concurrent.bin");

        var paths = await Task.WhenAll(
            Task.Run(() => cache.ResolveAsync(artifact)),
            Task.Run(() => cache.ResolveAsync(artifact)),
            Task.Run(() => cache.ResolveAsync(artifact)));

        Assert.All(paths, p => Assert.Equal(paths[0], p));
        Assert.Equal(payload, await File.ReadAllBytesAsync(paths[0]));
        Assert.Equal(1, handler.Calls);
    }

    // ── failure modes ───────────────────────────────────────────────────────

    [Fact]
    public async Task Hash_Mismatch_Deletes_Partial_And_Throws_With_Both_Hashes()
    {
        var payload = "tampered bytes"u8.ToArray();
        var expectedSha = new string('a', 64); // deliberately wrong
        var handler = new CountingHandler(payload);
        var cache = CacheWith(handler);
        var artifact = ArtifactFor(payload, sha: expectedSha);

        var ex = await Assert.ThrowsAsync<VoxaModelUnavailableException>(() => cache.ResolveAsync(artifact));

        Assert.Contains(expectedSha, ex.Message);
        Assert.Contains(Sha256Of(payload), ex.Message);
        var finalPath = cache.PathFor(artifact);
        Assert.False(File.Exists(finalPath));
        Assert.False(File.Exists(finalPath + ".partial"));
    }

    [Fact]
    public async Task Offline_Miss_Throws_With_Provisioning_Instructions_And_Never_Touches_Network()
    {
        var payload = "never downloaded"u8.ToArray();
        var handler = new CountingHandler(payload);
        var cache = CacheWith(handler, offline: true);
        var artifact = ArtifactFor(payload);

        var ex = await Assert.ThrowsAsync<VoxaModelUnavailableException>(() => cache.ResolveAsync(artifact));

        // The message is the air-gap runbook: artifact id, expected path, pinned URL, SHA-256.
        Assert.Contains(artifact.Id, ex.Message);
        Assert.Contains(cache.PathFor(artifact), ex.Message);
        Assert.Contains(artifact.DownloadUrl.ToString(), ex.Message);
        Assert.Contains(artifact.Sha256, ex.Message);
        Assert.Contains("Voxa:Models:Offline", ex.Message);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Offline_Hit_Resolves_Without_Network()
    {
        var payload = "pre-provisioned"u8.ToArray();
        var handler = new CountingHandler(payload);
        var artifact = ArtifactFor(payload);

        // Provision out-of-band (the air-gap path), then resolve offline.
        var online = CacheWith(new CountingHandler(payload));
        await online.ResolveAsync(artifact);

        var offline = CacheWith(handler, offline: true);
        var path = await offline.ResolveAsync(artifact);

        Assert.Equal(payload, await File.ReadAllBytesAsync(path));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void IsCached_Probe_Has_No_Side_Effects()
    {
        var payload = "probe"u8.ToArray();
        var cache = CacheWith(new CountingHandler(payload));
        var artifact = ArtifactFor(payload, id: "probe/never-created.bin");

        Assert.False(cache.IsCached(artifact));
        // Validate-time probing must not create directories (validation has no side effects).
        Assert.False(Directory.Exists(_root));
    }

    // ── archives ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Zip_Archive_Extracts_And_Resolves_The_Entry()
    {
        var entryContent = "i am a tool binary"u8.ToArray();
        byte[] zipBytes;
        using (var ms = new MemoryStream())
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                using (var es = zip.CreateEntry("piper/piper.exe").Open())
                    es.Write(entryContent);
                using (var ss = zip.CreateEntry("piper/espeak-ng-data/readme.txt").Open())
                    ss.Write("data"u8);
            }
            zipBytes = ms.ToArray();
        }

        var handler = new CountingHandler(zipBytes);
        var cache = CacheWith(handler);
        var artifact = ArtifactFor(zipBytes, id: "piper/piper_windows_amd64.zip", archiveEntry: "piper/piper.exe");

        var path = await cache.ResolveAsync(artifact);

        Assert.Equal(entryContent, await File.ReadAllBytesAsync(path));
        Assert.True(cache.IsCached(artifact));
        // Sibling data files extracted alongside (tool binaries need them)...
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "espeak-ng-data", "readme.txt")));
        // ...and the archive itself is deleted after extraction to reclaim disk.
        Assert.False(File.Exists(Path.Combine(_root, "piper", "piper_windows_amd64.zip")));

        await cache.ResolveAsync(artifact);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Archive_Missing_Declared_Entry_Throws_Catalog_Error()
    {
        byte[] zipBytes;
        using (var ms = new MemoryStream())
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = zip.CreateEntry("other/file.txt");
                using var es = entry.Open();
                es.Write("x"u8);
            }
            zipBytes = ms.ToArray();
        }

        var cache = CacheWith(new CountingHandler(zipBytes));
        var artifact = ArtifactFor(zipBytes, id: "tools/tool.zip", archiveEntry: "bin/missing.exe");

        var ex = await Assert.ThrowsAsync<VoxaModelUnavailableException>(() => cache.ResolveAsync(artifact));
        Assert.Contains("bin/missing.exe", ex.Message);
    }

    // ── options resolution ──────────────────────────────────────────────────

    private static IConfigurationSection VoxaSection(params (string Key, string Value)[] pairs)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => $"Voxa:{p.Key}", p => (string?)p.Value))
            .Build()
            .GetSection("Voxa");

    [Fact]
    public void Options_Default_To_OS_Cache_Root_And_Online()
    {
        var prior = Environment.GetEnvironmentVariable(VoxaModelCacheOptions.CacheRootEnvVar);
        Environment.SetEnvironmentVariable(VoxaModelCacheOptions.CacheRootEnvVar, null);
        try
        {
            var options = VoxaModelCacheOptions.FromConfiguration(VoxaSection());
            Assert.Equal(VoxaModelCacheOptions.DefaultCacheRoot(), options.CacheRoot);
            Assert.False(options.Offline);
        }
        finally
        {
            Environment.SetEnvironmentVariable(VoxaModelCacheOptions.CacheRootEnvVar, prior);
        }
    }

    [Fact]
    public void Options_Config_CachePath_And_Offline_Bind()
    {
        var prior = Environment.GetEnvironmentVariable(VoxaModelCacheOptions.CacheRootEnvVar);
        Environment.SetEnvironmentVariable(VoxaModelCacheOptions.CacheRootEnvVar, null);
        try
        {
            var options = VoxaModelCacheOptions.FromConfiguration(VoxaSection(
                ("Models:CachePath", @"X:\custom\cache"),
                ("Models:Offline", "true")));
            Assert.Equal(@"X:\custom\cache", options.CacheRoot);
            Assert.True(options.Offline);
        }
        finally
        {
            Environment.SetEnvironmentVariable(VoxaModelCacheOptions.CacheRootEnvVar, prior);
        }
    }

    [Fact]
    public void ResolveCacheRoot_Honors_The_Env_Var_Then_Falls_Back_To_Default()
    {
        // Regression: the LocalModels integration tests build a cache from ResolveCacheRoot(), and
        // CI points VOXA_MODEL_CACHE at the directory it caches. If this stopped honouring the env
        // var, tests would read a different directory than CI populates and fail network-blocked.
        var prior = Environment.GetEnvironmentVariable(VoxaModelCacheOptions.CacheRootEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(VoxaModelCacheOptions.CacheRootEnvVar, @"Z:\ci\models");
            Assert.Equal(@"Z:\ci\models", VoxaModelCacheOptions.ResolveCacheRoot());

            Environment.SetEnvironmentVariable(VoxaModelCacheOptions.CacheRootEnvVar, null);
            Assert.Equal(VoxaModelCacheOptions.DefaultCacheRoot(), VoxaModelCacheOptions.ResolveCacheRoot());
        }
        finally
        {
            Environment.SetEnvironmentVariable(VoxaModelCacheOptions.CacheRootEnvVar, prior);
        }
    }

    [Fact]
    public void Options_EnvVar_Overrides_Config()
    {
        var prior = Environment.GetEnvironmentVariable(VoxaModelCacheOptions.CacheRootEnvVar);
        Environment.SetEnvironmentVariable(VoxaModelCacheOptions.CacheRootEnvVar, @"Y:\env\cache");
        try
        {
            var options = VoxaModelCacheOptions.FromConfiguration(VoxaSection(
                ("Models:CachePath", @"X:\custom\cache")));
            Assert.Equal(@"Y:\env\cache", options.CacheRoot);
        }
        finally
        {
            Environment.SetEnvironmentVariable(VoxaModelCacheOptions.CacheRootEnvVar, prior);
        }
    }

    [Theory]
    [InlineData("../escape.bin")]
    [InlineData("a/../../escape.bin")]
    public void Path_Escapes_In_Catalog_Ids_Are_Rejected(string id)
    {
        var cache = new VoxaModelCache(new VoxaModelCacheOptions(_root, Offline: false));
        var artifact = new VoxaModelArtifact(id, new Uri("https://x.test/a"), new string('0', 64), 1);
        Assert.Throws<ArgumentException>(() => cache.PathFor(artifact));
    }

    // ── inventory (VST-001 WS0-A5) ──────────────────────────────────────────

    [Fact]
    public async Task Enumerate_Lists_Files_And_Extracted_Dirs_Skipping_Inflight_Noise()
    {
        var payload = "inventory model"u8.ToArray();
        var cache = CacheWith(new CountingHandler(payload));
        await cache.ResolveAsync(ArtifactFor(payload, id: "whisper/tiny.bin"));
        await cache.ResolveAsync(ArtifactFor(payload, id: "piper/voices/amy.onnx"));

        // In-flight noise that a live download would leave around — must not appear.
        await File.WriteAllBytesAsync(Path.Combine(_root, "whisper", "huge.bin.partial"), payload);
        await File.WriteAllTextAsync(Path.Combine(_root, "whisper", "huge.bin.lock"), "");

        // A fake extracted archive: one logical entry, recursive size.
        var extracted = Directory.CreateDirectory(Path.Combine(_root, "espeak", "espeak.tar.gz.extracted"));
        Directory.CreateDirectory(Path.Combine(extracted.FullName, "bin"));
        await File.WriteAllBytesAsync(Path.Combine(extracted.FullName, "bin", "espeak-ng"), new byte[10]);
        await File.WriteAllBytesAsync(Path.Combine(extracted.FullName, "readme.txt"), new byte[5]);

        var entries = new VoxaModelCache(new VoxaModelCacheOptions(_root, Offline: true)).Enumerate();

        Assert.Equal(3, entries.Count);
        Assert.Contains(entries, e => e.Id == "whisper/tiny.bin" && !e.IsExtractedArchive && e.SizeBytes == payload.Length);
        Assert.Contains(entries, e => e.Id == "piper/voices/amy.onnx");
        var archive = Assert.Single(entries, e => e.IsExtractedArchive);
        Assert.Equal("espeak/espeak.tar.gz.extracted", archive.Id);
        Assert.Equal(15, archive.SizeBytes);
    }

    [Fact]
    public async Task Verify_Detects_OnDisk_Corruption_And_Purge_Then_Resolve_Repairs()
    {
        var payload = "verify me"u8.ToArray();
        var handler = new CountingHandler(payload);
        var cache = CacheWith(handler);
        var artifact = ArtifactFor(payload, id: "test/verify.bin");

        var path = await cache.ResolveAsync(artifact);
        Assert.True(await cache.VerifyAsync(artifact));

        // Corrupt the cached file on disk — Verify must flag it.
        await File.WriteAllBytesAsync(path, "tampered!"u8.ToArray());
        Assert.False(await cache.VerifyAsync(artifact));

        // Purge + re-resolve repairs through the normal verified-download path.
        var entry = Assert.Single(cache.Enumerate(), e => e.Id == "test/verify.bin");
        cache.Purge(entry);
        Assert.False(cache.IsCached(artifact));
        await cache.ResolveAsync(artifact);
        Assert.True(await cache.VerifyAsync(artifact));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Verify_Returns_False_For_Uncached_And_True_For_Extracted_Entries()
    {
        var cache = new VoxaModelCache(new VoxaModelCacheOptions(_root, Offline: true));
        var missing = ArtifactFor("nope"u8.ToArray(), id: "test/missing.bin");
        Assert.False(await cache.VerifyAsync(missing));

        // Archive artifact: the pin covered the (deleted) archive — existence is the contract.
        var archive = ArtifactFor("zip"u8.ToArray(), id: "tool/pack.zip", archiveEntry: "bin/tool");
        var entryPath = cache.PathFor(archive);
        Directory.CreateDirectory(Path.GetDirectoryName(entryPath)!);
        await File.WriteAllBytesAsync(entryPath, new byte[3]);
        Assert.True(await cache.VerifyAsync(archive));
    }

    [Fact]
    public async Task Purge_Refuses_While_A_Download_Lock_Is_Held()
    {
        var payload = "locked"u8.ToArray();
        var cache = CacheWith(new CountingHandler(payload));
        var artifact = ArtifactFor(payload, id: "test/locked.bin");
        var path = await cache.ResolveAsync(artifact);

        // Simulate a concurrent downloader holding the lock.
        await File.WriteAllTextAsync(path + ".lock", "");
        var entry = Assert.Single(cache.Enumerate(), e => e.Id == "test/locked.bin");

        var ex = Assert.Throws<IOException>(() => cache.Purge(entry));
        Assert.Contains("lock", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(path)); // nothing was deleted

        File.Delete(path + ".lock");
        cache.Purge(entry);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Prefetch_Resolves_Each_Artifact_And_Reports_Progress()
    {
        var payload = "prefetch"u8.ToArray();
        var handler = new CountingHandler(payload);
        var cache = CacheWith(handler);
        var artifacts = new[]
        {
            ArtifactFor(payload, id: "set/a.bin"),
            ArtifactFor(payload, id: "set/b.bin"),
        };

        // Pre-cache one of them: prefetch must not re-download it.
        await cache.ResolveAsync(artifacts[0]);
        Assert.Equal(1, handler.Calls);

        var reports = new List<VoxaPrefetchProgress>();
        await cache.PrefetchAsync(artifacts, new SynchronousProgress(reports));

        Assert.Equal(2, handler.Calls); // only the uncached artifact hit the network
        Assert.True(cache.IsCached(artifacts[1]));
        Assert.Equal(4, reports.Count); // started + completed per artifact
        Assert.Equal(2, reports[^1].CompletedCount);
        Assert.Equal(2, reports[^1].TotalCount);
        Assert.True(reports[^1].Completed);
    }

    /// <summary>IProgress without the SynchronizationContext post — reports land synchronously.</summary>
    private sealed class SynchronousProgress : IProgress<VoxaPrefetchProgress>
    {
        private readonly List<VoxaPrefetchProgress> _reports;
        public SynchronousProgress(List<VoxaPrefetchProgress> reports) => _reports = reports;
        public void Report(VoxaPrefetchProgress value) { lock (_reports) _reports.Add(value); }
    }
}
