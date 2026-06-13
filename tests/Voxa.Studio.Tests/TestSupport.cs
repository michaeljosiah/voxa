using Avalonia;
using Avalonia.Headless;
using Microsoft.Extensions.Configuration;
using Voxa.Studio;
using Voxa.Studio.Audio;
using Voxa.Studio.Services;

[assembly: AvaloniaTestApplication(typeof(Voxa.Studio.Tests.TestAppBuilder))]

namespace Voxa.Studio.Tests;

/// <summary>Headless Avalonia bootstrap for [AvaloniaFact] tests — Skia-backed so frames render.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>Shared fixtures: the keyless local config Studio ships, rooted in a temp cache.</summary>
public static class TestSupport
{
    /// <summary>Studio's shipped appsettings shape, with the cache isolated to a temp dir.</summary>
    public static IConfiguration LocalConfig(string? cacheRoot = null, params (string Key, string Value)[] extra)
    {
        var pairs = new Dictionary<string, string?>
        {
            ["Voxa:Stt"] = "WhisperCpp",
            ["Voxa:Tts"] = "Piper",
            ["Voxa:Agent:Provider"] = "Echo",
            ["Voxa:WhisperCpp:Model"] = "tiny.en",
            ["Voxa:Piper:Voice"] = "en_US-amy-low",
            ["Voxa:Diagnostics:Enabled"] = "true",
            ["Voxa:Models:EagerWarmup"] = "false",
            ["Voxa:Models:CachePath"] = cacheRoot ?? TempDir(),
        };
        foreach (var (key, value) in extra) pairs[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(pairs).Build();
    }

    public static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "voxa-studio-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A throwaway activation-store path so tests never read/write the real ~/voxa-activations.json.</summary>
    public static string TempActivationsPath() => Path.Combine(TempDir(), "voxa-activations.json");

    /// <summary>
    /// Studio services with the null audio backend and an isolated cache. The cache-root env var
    /// is cleared for the duration so a CI lane's VOXA_MODEL_CACHE cannot leak into VM tests. The
    /// secrets/activation stores default to an in-memory + temp pair so VST-003 tests never touch the
    /// developer's real DPAPI blob or activation file (and the Windows lane stays deterministic).
    /// </summary>
    public static StudioServices Services(
        string? cacheRoot = null,
        ISecretsStore? secrets = null,
        ProviderActivationStore? activations = null)
    {
        var prior = Environment.GetEnvironmentVariable("VOXA_MODEL_CACHE");
        Environment.SetEnvironmentVariable("VOXA_MODEL_CACHE", null);
        try
        {
            return new StudioServices(
                LocalConfig(cacheRoot),
                new NullAudioDevice(),
                secrets ?? new MemorySecretsStore(),
                activations ?? new ProviderActivationStore(TempActivationsPath()));
        }
        finally
        {
            Environment.SetEnvironmentVariable("VOXA_MODEL_CACHE", prior);
        }
    }
}
