using Microsoft.Extensions.Configuration;
using Voxa.Studio.Audio;

namespace Voxa.Studio.Services;

/// <summary>
/// The work behind the splash (VST-002 §4): each stage is REAL initialization, reported by
/// name so the splash microcopy ticks truthfully — never a vanity delay. Hard rules live
/// here: no network on any stage (device enumeration and cache scan are local; the container
/// build registers providers but resolves nothing remote), and the caller dismisses the
/// splash the moment this completes, even mid-animation.
///
/// Avalonia-free so headless tests can run the exact boot path the splash drives.
/// </summary>
public static class StartupCoordinator
{
    /// <summary>The stage names the splash displays, in order. "ready" is reported last.</summary>
    public static readonly IReadOnlyList<string> Stages =
        ["configuration", "providers", "devices", "model cache", "ready"];

    /// <summary>
    /// Run the staged boot. Call from a background thread (stages do disk and COM work);
    /// <paramref name="onStage"/> fires before each phase with its display name.
    /// </summary>
    public static StudioServices Run(Action<string>? onStage = null,
        IConfiguration? configuration = null, IStudioAudioDevice? audioDevice = null)
    {
        onStage?.Invoke("configuration");
        configuration ??= new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddEnvironmentVariables()
            .Build();

        onStage?.Invoke("providers");
        var services = new StudioServices(configuration, audioDevice);

        onStage?.Invoke("devices");
        // Warm the WASAPI COM enumeration so the Talk view's pickers populate instantly.
        _ = services.AudioDevice.CaptureEndpoints();
        _ = services.AudioDevice.RenderEndpoints();

        onStage?.Invoke("model cache");
        // Local disk scan only — what's already cached, never what could be downloaded.
        _ = services.ModelCache.Enumerate().Count();

        onStage?.Invoke("ready");
        return services;
    }
}
