using Avalonia;
using Avalonia.Headless;
using Microsoft.Extensions.Configuration;
using Voxa.Speech.Voxtral;
using Voxa.Studio;
using Voxa.Studio.Audio;
using Voxa.Studio.Services;

[assembly: AvaloniaTestApplication(typeof(Voxa.Studio.Tests.TestAppBuilder))]

namespace Voxa.Studio.Tests;

/// <summary>
/// Headless Avalonia bootstrap for [AvaloniaFact] tests.
///
/// <para>
/// Drawing backend is the load-bearing choice here. <c>UseHeadlessDrawing = false</c> (real Skia
/// rendering) starts a continuous headless RENDER LOOP that keeps the Avalonia dispatcher busy after
/// the tests finish — so the per-assembly <c>HeadlessUnitTestSession</c> never goes idle, its
/// dispatcher thread never terminates, and the xUnit runner blocks forever waiting on it. That is the
/// exact "all tests pass, then <c>dotnet test</c> never exits" hang that burned multiple 6h CI runs
/// and the v0.6.0-alpha release attempts (confirmed from a hang dump: the runner parked in
/// <c>RunTestsInAssembly</c>, the session dispatcher still live).
/// </para>
/// <para>
/// So default to <b>headless drawing</b> (no render loop → the host exits cleanly the instant tests
/// finish). Real Skia pixels are only needed by the opt-in screenshot utility (<c>VOXA_STUDIO_CAPTURE=1</c>,
/// which reads back rendered Avalonia windows via <c>CaptureRenderedFrame</c>); it enables Skia on demand
/// and accepts that the render loop then keeps the host alive until the (manual) run is killed.
/// (<c>VOXA_BRAND_EXPORT</c> draws the NuGet icon with raw SkiaSharp, not Avalonia rendering, so it does
/// NOT need this and stays on the fast, clean-exit path.)
/// </para>
/// </summary>
public static class TestAppBuilder
{
    /// <summary>True when the screenshot utility asked for real Skia pixels (and the render loop).</summary>
    public static bool RealPixels { get; } =
        Environment.GetEnvironmentVariable("VOXA_STUDIO_CAPTURE") == "1";

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = !RealPixels });
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

    /// <summary>A throwaway pipeline-profiles path so tests never touch the real ~/voxa-pipelines.json.</summary>
    public static string TempProfilesPath() => Path.Combine(TempDir(), "voxa-pipelines.json");

    /// <summary>
    /// Studio services with the null audio backend and an isolated cache. The cache-root env var
    /// is cleared for the duration so a CI lane's VOXA_MODEL_CACHE cannot leak into VM tests. The
    /// secrets/activation stores default to an in-memory + temp pair so VST-003 tests never touch the
    /// developer's real DPAPI blob or activation file (and the Windows lane stays deterministic).
    /// </summary>
    public static StudioServices Services(
        string? cacheRoot = null,
        ISecretsStore? secrets = null,
        ProviderActivationStore? activations = null,
        PipelineProfileStore? profiles = null)
    {
        var prior = Environment.GetEnvironmentVariable("VOXA_MODEL_CACHE");
        Environment.SetEnvironmentVariable("VOXA_MODEL_CACHE", null);
        try
        {
            return new StudioServices(
                LocalConfig(cacheRoot),
                new NullAudioDevice(),
                secrets ?? new MemorySecretsStore(),
                activations ?? new ProviderActivationStore(TempActivationsPath()),
                profiles ?? new PipelineProfileStore(TempProfilesPath()),
                new NoGpu());   // tests never shell out to nvidia-smi — keep the default-STT gate deterministic
        }
        finally
        {
            Environment.SetEnvironmentVariable("VOXA_MODEL_CACHE", prior);
        }
    }

    /// <summary>A GPU probe reporting no device, so the VLS-009 default-STT gate stays on whisper.cpp in tests
    /// (the selector's own tests inject a capable probe explicitly).</summary>
    private sealed class NoGpu : IGpuInfoProbe
    {
        public int LargestGpuMemoryGb() => 0;
    }
}
