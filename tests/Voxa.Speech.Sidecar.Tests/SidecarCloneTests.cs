using Voxa.Speech.Voices;

namespace Voxa.Speech.Sidecar.Tests;

/// <summary>
/// Local cloning for the sidecar provider (VVL-002): persist a reference clip, hand back its path as the
/// voice, and clean up safely — all pure file IO, no sidecar process.
/// </summary>
public class SidecarCloneTests
{
    private static string TempDir()
        => Path.Combine(Path.GetTempPath(), "voxa-sidecar-clone-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Clone_Persists_The_Reference_Clip_And_Returns_A_Cloned_Voice()
    {
        var dir = TempDir();
        var provider = new SidecarVoiceCloneProvider(dir);
        try
        {
            var voice = await provider.CreateVoiceAsync(
                new VoiceCloneRequest("My Voice", [new VoiceSample("ref.wav", new byte[] { 1, 2, 3, 4 })], Language: "en"),
                CancellationToken.None);

            Assert.Equal(VoiceKind.Cloned, voice.Kind);
            Assert.Equal("Sidecar", voice.ProviderName);
            Assert.Equal("My Voice", voice.DisplayName);
            Assert.Equal("en", voice.Language);
            Assert.True(File.Exists(voice.Id)); // Id is the persisted clip path the engine will reference
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(voice.Id));
            Assert.StartsWith(Path.GetFullPath(dir), Path.GetFullPath(voice.Id));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Clone_Without_Samples_Is_Rejected()
    {
        var provider = new SidecarVoiceCloneProvider(TempDir());
        await Assert.ThrowsAsync<VoiceProviderException>(() =>
            provider.CreateVoiceAsync(new VoiceCloneRequest("x", []), CancellationToken.None));
    }

    [Fact]
    public async Task Delete_Removes_The_Clip()
    {
        var dir = TempDir();
        var provider = new SidecarVoiceCloneProvider(dir);
        try
        {
            var voice = await provider.CreateVoiceAsync(
                new VoiceCloneRequest("v", [new VoiceSample("r.wav", new byte[] { 9 })]), CancellationToken.None);
            Assert.True(File.Exists(voice.Id));

            await provider.DeleteVoiceAsync(voice.Id, CancellationToken.None);
            Assert.False(File.Exists(voice.Id));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Delete_Refuses_A_Path_Outside_The_Voices_Directory()
    {
        var provider = new SidecarVoiceCloneProvider(TempDir());
        var outside = Path.Combine(Path.GetTempPath(), "voxa-not-a-voice.wav");
        await Assert.ThrowsAsync<VoiceProviderException>(() =>
            provider.DeleteVoiceAsync(outside, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_Works_When_The_Voices_Root_Has_A_Trailing_Separator()
    {
        // Regression (Codex P2): a configured root ending in a separator must still delete the provider's
        // own returned voice id — the containment check must normalize the root, not reject a valid clip.
        var dir = TempDir() + Path.DirectorySeparatorChar;
        var provider = new SidecarVoiceCloneProvider(dir);
        try
        {
            var voice = await provider.CreateVoiceAsync(
                new VoiceCloneRequest("v", [new VoiceSample("r.wav", new byte[] { 7 })]), CancellationToken.None);
            Assert.True(File.Exists(voice.Id));

            await provider.DeleteVoiceAsync(voice.Id, CancellationToken.None);
            Assert.False(File.Exists(voice.Id));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Descriptor_Exposes_The_Cloner()
    {
        Assert.NotNull(SidecarDescriptors.Tts.ResolveCloner);
    }
}
