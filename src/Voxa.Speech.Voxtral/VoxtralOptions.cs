using Microsoft.Extensions.Configuration;

namespace Voxa.Speech.Voxtral;

/// <summary>
/// Options for <see cref="VoxtralRealtimeSttEngine"/>, bound from <c>Voxa:Voxtral</c> (VLS-009). Voxtral is an
/// open-weights audio LLM served locally by vLLM over its realtime WebSocket API, so this configures how to reach
/// that server. Two honest hosting modes, like the VVL-002 sidecar:
/// <list type="bullet">
///   <item><b>Connect-only</b> — set <see cref="ServerUrl"/> to a vLLM realtime server you already run
///     (the documented <c>vllm serve</c> / container command). Voxa launches nothing.</item>
///   <item><b>Managed</b> — set <see cref="LaunchCommand"/> (or <see cref="ExecutablePath"/>) and
///     <see cref="LaunchArgs"/> so Voxa starts and owns the server process.</item>
/// </list>
/// </summary>
public sealed class VoxtralOptions
{
    /// <summary>Connect-only: ws(s) base URL of an already-running vLLM realtime server (e.g. <c>ws://127.0.0.1:8000</c>).
    /// When set, Voxa connects instead of launching, and <see cref="Host"/>/<see cref="Port"/> are ignored.</summary>
    public string? ServerUrl { get; set; }

    /// <summary>Managed mode: a launcher executable (e.g. a frozen vLLM wrapper). Wins over <see cref="LaunchCommand"/>.</summary>
    public string? ExecutablePath { get; set; }

    /// <summary>Managed mode: a launch command on PATH (e.g. <c>vllm</c> or <c>podman</c>).</summary>
    public string? LaunchCommand { get; set; }

    /// <summary>Arguments for the managed launch target (e.g. <c>serve mistralai/Voxtral-Mini-4B-Realtime-2602 ...</c>).</summary>
    public IReadOnlyList<string> LaunchArgs { get; set; } = [];

    /// <summary>Host the managed server listens on / the connect target. Default <c>127.0.0.1</c>.</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>Port the managed server listens on / the connect target. Default <c>8000</c>.</summary>
    public int Port { get; set; } = 8000;

    /// <summary>Model id sent in the <c>session.update</c> handshake.</summary>
    public string Model { get; set; } = "mistralai/Voxtral-Mini-4B-Realtime-2602";

    /// <summary>PCM sample rate the engine streams to the server. Voxtral Realtime expects 16 kHz mono PCM16.</summary>
    public int InputSampleRate { get; set; } = 16000;

    /// <summary>Realtime delay knob in ms (the model supports 80–2400; 480 is Mistral's recommendation).</summary>
    public int DelayMs { get; set; } = 480;

    /// <summary>Optional BCP-47 language hint; null lets the model auto-detect.</summary>
    public string? Language { get; set; }

    /// <summary>How long to wait for a <em>managed</em> server to become ready (a cold 4B load is slow). Connect-only
    /// mode ignores this — it connects to a server assumed already up.</summary>
    public int ReadyTimeoutSeconds { get; set; } = 180;

    /// <summary>VRAM floor (GiB) for the Studio GPU-gated default: a machine below this keeps whisper.cpp. The model
    /// needs a single GPU with >= 16 GB, so that is the default gate.</summary>
    public int MinGpuMemoryGb { get; set; } = 16;

    /// <summary>True when a managed launch target (executable or command) is configured.</summary>
    public bool HasManagedLaunch
        => !string.IsNullOrEmpty(ExecutablePath) || !string.IsNullOrEmpty(LaunchCommand);

    /// <summary>True when any hosting mode is resolvable (connect-only or managed) — the validation gate.</summary>
    public bool HasHostingMode => !string.IsNullOrEmpty(ServerUrl) || HasManagedLaunch;

    /// <summary>The realtime WebSocket endpoint to connect to: the configured <see cref="ServerUrl"/> (connect-only)
    /// or <c>ws://{Host}:{Port}</c> (managed), with the <c>/v1/realtime</c> route appended when absent.</summary>
    public Uri ResolveEndpoint()
    {
        var baseUrl = (string.IsNullOrEmpty(ServerUrl) ? $"ws://{Host}:{Port}" : ServerUrl).TrimEnd('/');
        var full = baseUrl.EndsWith("/v1/realtime", StringComparison.OrdinalIgnoreCase)
            ? baseUrl
            : baseUrl + "/v1/realtime";
        return new Uri(full, UriKind.Absolute);
    }

    /// <summary>The http(s) base used to poll the managed server's <c>/health</c> route during readiness wait.</summary>
    public Uri ResolveHealthEndpoint()
    {
        var endpoint = ResolveEndpoint();
        var http = endpoint.Scheme == "wss" ? "https" : "http";
        return new Uri($"{http}://{endpoint.Authority}/health");
    }

    /// <summary>Bind from the root <c>"Voxa"</c> configuration section.</summary>
    public static VoxtralOptions FromConfiguration(IConfigurationSection voxaRoot)
    {
        var s = voxaRoot.GetSection("Voxtral");
        return new VoxtralOptions
        {
            ServerUrl           = s["ServerUrl"],
            ExecutablePath      = s["ExecutablePath"],
            LaunchCommand       = s["LaunchCommand"],
            LaunchArgs          = s.GetSection("LaunchArgs").Get<string[]>() ?? [],
            Host                = s["Host"] ?? "127.0.0.1",
            Port                = s.GetValue("Port", 8000),
            Model               = s["Model"] ?? "mistralai/Voxtral-Mini-4B-Realtime-2602",
            InputSampleRate     = s.GetValue("InputSampleRate", 16000),
            DelayMs             = s.GetValue("DelayMs", 480),
            Language            = s["Language"],
            ReadyTimeoutSeconds = s.GetValue("ReadyTimeoutSeconds", 180),
            MinGpuMemoryGb      = s.GetValue("MinGpuMemoryGb", 16),
        };
    }
}
