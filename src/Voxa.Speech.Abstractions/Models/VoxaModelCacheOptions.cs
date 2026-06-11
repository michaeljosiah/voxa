using Microsoft.Extensions.Configuration;

namespace Voxa.Speech;

/// <summary>
/// Settings for <see cref="VoxaModelCache"/>. Bound from the <c>Voxa:Models</c> section via
/// <see cref="FromConfiguration"/>; the <c>VOXA_MODEL_CACHE</c> environment variable overrides
/// the configured cache path (the CI- and container-friendly knob).
/// </summary>
/// <param name="CacheRoot">Directory all artifacts are cached under.</param>
/// <param name="Offline">
/// When true the cache never downloads: a missing artifact is a
/// <see cref="VoxaModelUnavailableException"/> whose message is a copy-pasteable air-gap
/// provisioning instruction (expected path, pinned URL, SHA-256).
/// </param>
public sealed record VoxaModelCacheOptions(string CacheRoot, bool Offline)
{
    /// <summary>Environment variable that overrides the cache root with highest precedence.</summary>
    public const string CacheRootEnvVar = "VOXA_MODEL_CACHE";

    /// <summary>
    /// Resolution: <c>VOXA_MODEL_CACHE</c> env var → <c>Voxa:Models:CachePath</c> → OS default
    /// (<c>%LOCALAPPDATA%\voxa\models</c> on Windows, <c>$XDG_CACHE_HOME/voxa/models</c> or
    /// <c>~/.cache/voxa/models</c> elsewhere). <paramref name="voxaRoot"/> is the root
    /// <c>"Voxa"</c> configuration section.
    /// </summary>
    public static VoxaModelCacheOptions FromConfiguration(IConfigurationSection voxaRoot)
    {
        ArgumentNullException.ThrowIfNull(voxaRoot);
        var models = voxaRoot.GetSection("Models");

        var root = Environment.GetEnvironmentVariable(CacheRootEnvVar);
        if (string.IsNullOrWhiteSpace(root)) root = models["CachePath"];
        if (string.IsNullOrWhiteSpace(root)) root = DefaultCacheRoot();

        return new VoxaModelCacheOptions(root, models.GetValue("Offline", false));
    }

    /// <summary>The OS-conventional cache directory used when nothing overrides it.</summary>
    public static string DefaultCacheRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "voxa", "models");
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        var cacheHome = !string.IsNullOrWhiteSpace(xdg)
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        return Path.Combine(cacheHome, "voxa", "models");
    }
}
