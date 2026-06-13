using Voxa.Speech.Voices;

namespace Voxa.Studio.Services;

/// <summary>
/// The on-disk voice library (VVL-001 WS4): one <c>&lt;id&gt;.json</c> per profile under
/// <c>~/voxa-voices</c>, with each clone's reference clips beside it in <c>&lt;id&gt;/samples/</c>.
/// Mirrors <see cref="RunStore"/> exactly — folder scan, corrupt files skipped, a constructor
/// override as the test seam (no config key). Nothing here ever holds a secret.
/// </summary>
public sealed class VoiceStore
{
    private readonly string _dir;

    public VoiceStore(string? dir = null) =>
        _dir = dir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "voxa-voices");

    public string Directory => _dir;

    /// <summary>All profiles, newest first. An unreadable/hand-edited file is skipped, not fatal.</summary>
    public IReadOnlyList<VoiceProfile> Load()
    {
        if (!System.IO.Directory.Exists(_dir)) return [];
        var profiles = new List<VoiceProfile>();
        foreach (var file in System.IO.Directory.EnumerateFiles(_dir, "*.json"))
        {
            try { profiles.Add(VoiceProfile.FromJson(File.ReadAllText(file))); }
            catch { /* a truncated or hand-edited profile must not break the library */ }
        }
        return profiles.OrderByDescending(p => p.CreatedAt).ToList();
    }

    /// <summary>
    /// Persist a profile and, optionally, the reference samples it was cloned from. Sample bytes
    /// are written under <c>&lt;id&gt;/samples/</c> and the saved profile's <see cref="VoiceProfile.SamplePaths"/>
    /// points at them. Returns the stored profile (with resolved sample paths).
    /// </summary>
    public VoiceProfile Save(VoiceProfile profile, IReadOnlyList<VoiceSample>? samplesToPersist = null)
    {
        System.IO.Directory.CreateDirectory(_dir);

        var stored = profile;
        if (samplesToPersist is { Count: > 0 })
        {
            var samplesDir = Path.Combine(_dir, profile.Id, "samples");
            System.IO.Directory.CreateDirectory(samplesDir);
            var paths = new List<string>(samplesToPersist.Count);
            for (var i = 0; i < samplesToPersist.Count; i++)
            {
                var sample = samplesToPersist[i];
                var name = SafeFileName(sample.FileName, fallback: $"sample-{i + 1}.wav");
                var path = Path.Combine(samplesDir, name);
                File.WriteAllBytes(path, sample.Data.ToArray());
                paths.Add(path);
            }
            stored = profile with { SamplePaths = paths };
        }

        File.WriteAllText(Path.Combine(_dir, $"{profile.Id}.json"), stored.ToJson());
        return stored;
    }

    /// <summary>Remove a profile's JSON and its samples directory.</summary>
    public void Delete(VoiceProfile profile)
    {
        var json = Path.Combine(_dir, $"{profile.Id}.json");
        if (File.Exists(json)) File.Delete(json);

        var samplesDir = Path.Combine(_dir, profile.Id);
        if (System.IO.Directory.Exists(samplesDir)) System.IO.Directory.Delete(samplesDir, recursive: true);
    }

    // Keep only the file name and strip anything path-like, so a provider-supplied name can never
    // escape the samples directory.
    private static string SafeFileName(string name, string fallback)
    {
        var bare = Path.GetFileName(name);
        if (string.IsNullOrWhiteSpace(bare)) return fallback;
        foreach (var c in Path.GetInvalidFileNameChars()) bare = bare.Replace(c, '_');
        return bare;
    }
}
