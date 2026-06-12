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
    public void Empty_Cache_Reports_Itself_And_The_Effective_Root()
    {
        var cacheRoot = TestSupport.TempDir();
        var vm = new ModelsViewModel(TestSupport.Services(cacheRoot));

        Assert.Empty(vm.Rows);
        Assert.Equal(cacheRoot, vm.CacheRoot);
        Assert.Equal("from Voxa:Models:CachePath", vm.CacheRootSource);
        Assert.Contains("empty", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }
}
