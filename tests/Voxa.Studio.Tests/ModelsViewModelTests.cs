using Voxa.Studio.ViewModels;

namespace Voxa.Studio.Tests;

/// <summary>
/// VST-001 WS4-A1: the Models view against a real temp cache directory — inventory joins the
/// catalogs, corruption shows ✗ on verify, purge removes, and a held download lock refuses
/// purge with a readable message.
/// </summary>
public class ModelsViewModelTests
{
    [Fact]
    public async Task Inventory_Verify_Corruption_Purge_And_Lock_Refusal()
    {
        var cacheRoot = TestSupport.TempDir();
        var services = TestSupport.Services(cacheRoot);

        // Plant a "downloaded" whisper model with WRONG bytes (a corrupted cache entry), plus a
        // foreign file no catalog knows.
        var whisperPath = Path.Combine(cacheRoot, "whisper", "ggml-tiny.en.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(whisperPath)!);
        await File.WriteAllBytesAsync(whisperPath, "definitely not a ggml model"u8.ToArray());
        await File.WriteAllBytesAsync(Path.Combine(cacheRoot, "mystery.bin"), new byte[16]);

        var vm = new ModelsViewModel(services);

        Assert.Equal(2, vm.Rows.Count);
        var whisper = Assert.Single(vm.Rows, r => r.Id == "whisper/ggml-tiny.en.bin");
        Assert.True(whisper.IsKnown);
        Assert.Equal("Whisper", whisper.EngineLabel);
        var mystery = Assert.Single(vm.Rows, r => r.Id == "mystery.bin");
        Assert.False(mystery.IsKnown);

        // Corruption is caught by the streamed re-hash.
        await vm.VerifyCommand.ExecuteAsync(whisper);
        Assert.Equal("✗", whisper.VerifyState);

        // A concurrent download's lock refuses the purge and deletes nothing.
        var lockPath = whisperPath + ".lock";
        await File.WriteAllTextAsync(lockPath, "");
        vm.PurgeCommand.Execute(whisper);
        Assert.NotNull(vm.ErrorText);
        Assert.Contains("lock", vm.ErrorText, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(whisperPath));

        // Lock released → purge succeeds and the row disappears.
        File.Delete(lockPath);
        vm.ErrorText = null;
        var row = vm.Rows.Single(r => r.Id == "whisper/ggml-tiny.en.bin");
        vm.PurgeCommand.Execute(row);
        Assert.False(File.Exists(whisperPath));
        Assert.DoesNotContain(vm.Rows, r => r.Id == "whisper/ggml-tiny.en.bin");
    }

    [Fact]
    public async Task Bulk_Prefetch_Survives_A_Failing_Artifact_And_Lists_The_Casualty()
    {
        // Regression: one stale pin (a SHA-256 mismatch on download) used to abort the entire
        // "Prefetch full catalog" run — 1 bad artifact cancelled the other 20-odd downloads.
        // Bulk provisioning must fetch each independently and report failures at the end.
        var vm = new ModelsViewModel(TestSupport.Services());
        var artifacts = Voxa.Speech.WhisperCpp.WhisperCppModelCatalog.KnownModels
            .Take(3)
            .Select(m => { Voxa.Speech.WhisperCpp.WhisperCppModelCatalog.TryGet(m, out var a); return a; })
            .ToList();

        var fetched = new List<string>();
        var failures = await vm.PrefetchEachAsync(artifacts, a =>
        {
            if (a.Id == artifacts[1].Id)
                throw new InvalidOperationException($"Downloaded artifact '{a.Id}' failed SHA-256 verification.");
            fetched.Add(a.Id);
            return Task.CompletedTask;
        });

        Assert.Equal([artifacts[0].Id, artifacts[2].Id], fetched); // the rest still downloaded
        var failure = Assert.Single(failures);
        Assert.Equal(artifacts[1].Id, failure.Id);
        Assert.Contains("SHA-256", failure.Error);
        Assert.Equal(1, vm.PrefetchProgress); // the bar reached the end despite the casualty
    }

    [Fact]
    public void Empty_Cache_Reports_Itself_And_The_Effective_Root()
    {
        var cacheRoot = TestSupport.TempDir();
        var vm = new ModelsViewModel(TestSupport.Services(cacheRoot));

        Assert.Empty(vm.Rows);
        Assert.Equal(cacheRoot, vm.CacheRoot);
        Assert.Equal("from Voxa:Models:CachePath", vm.CacheRootSource);
        Assert.Contains("empty", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Tabs_Group_By_Category_And_Each_Filters_By_Provider()
    {
        var cacheRoot = TestSupport.TempDir();
        foreach (var rel in new[]
        {
            "whisper/ggml-tiny.en.bin", "piper/en_US-amy-low.onnx", "kokoro/model.onnx", "mystery.bin",
        })
        {
            var p = Path.Combine(cacheRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            await File.WriteAllBytesAsync(p, new byte[8]);
        }
        var vm = new ModelsViewModel(TestSupport.Services(cacheRoot));

        // "All" shows every cache entry.
        Assert.Equal("All", vm.SelectedTab);
        Assert.Equal(4, vm.VisibleRows.Count);

        // TTS tab groups Piper + Kokoro, and the provider filter offers exactly those.
        vm.SelectedTab = "TTS";
        Assert.Equal(2, vm.VisibleRows.Count);
        Assert.All(vm.VisibleRows, r => Assert.Equal("TTS", r.Category));
        Assert.Contains("Piper", vm.ProviderFilters);
        Assert.Contains("Kokoro", vm.ProviderFilters);
        Assert.DoesNotContain("Whisper", vm.ProviderFilters);

        // Filter within the tab to a single provider.
        vm.SelectedProvider = "Piper";
        Assert.Equal("Piper", Assert.Single(vm.VisibleRows).Provider);

        // Switching tabs resets a now-invalid provider selection and re-groups.
        vm.SelectedTab = "STT";
        Assert.Equal("All providers", vm.SelectedProvider);
        Assert.Equal("STT", Assert.Single(vm.VisibleRows).Category);
    }
}
