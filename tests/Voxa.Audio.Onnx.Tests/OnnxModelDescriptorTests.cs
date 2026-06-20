using Voxa.Speech;

namespace Voxa.Audio.Onnx.Tests;

public class OnnxModelDescriptorTests
{
    // SHA-256 is irrelevant for an already-present (cache-hit) file; ResolveAsync only verifies on download.
    private static VoxaModelArtifact Artifact(string id) =>
        new(id, new Uri("https://example.invalid/" + id), Sha256: "00", SizeBytes: 1);

    [Fact]
    public async Task ResolveAsync_returns_graph_and_sidecar_paths_for_already_cached_files()
    {
        using var temp = new TempDir();
        // Pre-place the files so ResolveAsync is a pure cache hit — no network, offline-safe.
        File.WriteAllText(Path.Combine(temp.Path, "graph.onnx"), "graph");
        File.WriteAllText(Path.Combine(temp.Path, "vocab.txt"), "vocab");

        var cache = new VoxaModelCache(new VoxaModelCacheOptions(temp.Path, Offline: true));
        var model = new OnnxModelDescriptor(
            "m", Artifact("graph.onnx"), [Artifact("vocab.txt")], [OnnxDevice.Cpu]);

        var resolved = await model.ResolveAsync(cache);

        Assert.Equal(Path.Combine(temp.Path, "graph.onnx"), resolved.GraphPath);
        Assert.Equal(Path.Combine(temp.Path, "vocab.txt"), resolved.Sidecars["vocab.txt"]);
        Assert.Single(resolved.Sidecars);
    }

    [Fact]
    public async Task ResolveAsync_with_no_sidecars_returns_just_the_graph()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "graph.onnx"), "graph");

        var cache = new VoxaModelCache(new VoxaModelCacheOptions(temp.Path, Offline: true));
        var model = new OnnxModelDescriptor("m", Artifact("graph.onnx"), [], [OnnxDevice.Cpu]);

        var resolved = await model.ResolveAsync(cache);

        Assert.Equal(Path.Combine(temp.Path, "graph.onnx"), resolved.GraphPath);
        Assert.Empty(resolved.Sidecars);
    }

    [Fact]
    public async Task ResolveAsync_throws_for_a_missing_offline_artifact()
    {
        using var temp = new TempDir();
        var cache = new VoxaModelCache(new VoxaModelCacheOptions(temp.Path, Offline: true));
        var model = new OnnxModelDescriptor("m", Artifact("missing.onnx"), [], [OnnxDevice.Cpu]);

        await Assert.ThrowsAsync<VoxaModelUnavailableException>(() => model.ResolveAsync(cache));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "voxa-onnx-test-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
