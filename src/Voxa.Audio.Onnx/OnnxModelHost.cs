using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using Voxa.Speech;

namespace Voxa.Audio.Onnx;

/// <summary>
/// Process-wide host for ONNX Runtime sessions (VLS-006 WS1). Owns the <see cref="InferenceSession"/>
/// lifecycle so consuming engines never construct one directly: weights load once per
/// <c>(resolved path, device)</c> and are shared by every connection on the host — the same process-wide
/// cache shape as <c>KokoroTtsEngine</c> and the whisper.cpp factory cache.
///
/// <para>
/// <b>Construction is thread-safe; inference is the caller's contract.</b> The <see cref="Lazy{T}"/>
/// guarantees a model loads exactly once under concurrent <see cref="Load"/> calls. A model with reused
/// per-instance buffers (Silero's LSTM state) is still not safe to <c>Run</c> from multiple threads — that
/// constraint stays with the engine, exactly as today.
/// </para>
///
/// <para>
/// <b>Disposal:</b> cached sessions are process-lifetime by design (they hold weights shared across
/// connections), so the host has no per-session dispose — a connection ending must not tear a shared
/// session down. <see cref="EvictAll"/> disposes and clears every cached session for tests and Studio's
/// "unload models."
/// </para>
/// </summary>
public sealed class OnnxModelHost
{
    // Process-wide: model weights load once per (path, device), shared across connections AND across
    // OnnxModelHost instances. Deliberately static (not a per-host field) — the same rationale as
    // KokoroTtsEngine.Sessions.
    private static readonly ConcurrentDictionary<(string Path, OnnxDevice Device), Lazy<IOnnxSession>>
        Sessions = new();

    private readonly ILogger _logger;

    public OnnxModelHost(ILogger<OnnxModelHost>? logger = null)
        => _logger = logger ?? NullLogger<OnnxModelHost>.Instance;

    /// <summary>
    /// Load — or return the cached — session for a model file. <paramref name="modelPath"/> must already be
    /// resolved and verified (e.g. via <see cref="OnnxModelDescriptorExtensions.ResolveAsync"/> /
    /// <c>VoxaModelCache</c>); the host does not download. <paramref name="hook"/> runs after the device EP
    /// is applied and before the session is built, so a model can install a custom EP or tune
    /// <see cref="SessionOptions"/> — but it runs only on the <b>first</b> load of a given
    /// <c>(path, device)</c>; a cache hit returns the existing session and ignores the hook (the session is
    /// configured once and shared).
    /// </summary>
    /// <exception cref="VoxaModelUnavailableException">An explicit GPU <paramref name="device"/> whose runtime isn't loaded.</exception>
    public IOnnxSession Load(
        string modelPath,
        OnnxDevice device = OnnxDevice.Cpu,
        OnnxSessionOptionsHook? hook = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelPath);
        var fullPath = Path.GetFullPath(modelPath);
        // Windows filesystems are case-insensitive but case-preserving, so two differently-cased paths to
        // the same file must map to ONE cache entry or the weights load twice. Normalise the key (not the
        // load path) on Windows; keep it verbatim where the filesystem is case-sensitive (Linux/macOS).
        var keyPath = OperatingSystem.IsWindows() ? fullPath.ToLowerInvariant() : fullPath;
        var key = (keyPath, device);

        // The Lazy factory runs OUTSIDE the dictionary lock, so a slow model load doesn't block other
        // keys, and the loser on the same key still receives the winner's single instance. The factory
        // loads from the real-cased fullPath, not the normalised key path.
        var lazy = Sessions.GetOrAdd(key, _ => new Lazy<IOnnxSession>(() => CreateSession(fullPath, device, hook)));
        try
        {
            return lazy.Value;
        }
        catch
        {
            // A throwing load (e.g. a bad explicit GPU device) must not poison the cache — drop this exact
            // faulted entry so the next Load retries cleanly. TryRemove(KeyValuePair) only removes if the
            // value is still our faulted Lazy, never a concurrent successful retry.
            Sessions.TryRemove(new KeyValuePair<(string, OnnxDevice), Lazy<IOnnxSession>>(key, lazy));
            throw;
        }
    }

    /// <summary>
    /// Dispose and forget every cached session (tests, Studio's "unload models"). NOT for per-connection
    /// teardown — it tears down weights other live connections may still be using.
    /// </summary>
    public static void EvictAll()
    {
        foreach (var key in Sessions.Keys.ToArray())
        {
            if (Sessions.TryRemove(key, out var lazy) && lazy.IsValueCreated)
                lazy.Value.Session.Dispose();
        }
    }

    private IOnnxSession CreateSession(string path, OnnxDevice device, OnnxSessionOptionsHook? hook)
    {
        // SessionOptions can be disposed once the session is constructed (ORT copies what it needs),
        // matching SileroVadEngine.
        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, // matches Kokoro today
        };
        var active = OnnxExecutionProvider.Apply(options, device, _logger);
        hook?.Invoke(options, path, device);

        var session = new InferenceSession(path, options);
        _logger.LogInformation("Onnx host: loaded '{Model}' on {Device}.", Path.GetFileName(path), active);
        return new LoadedOnnxSession(session, active);
    }
}

/// <summary>
/// Customisation callback for a session's <see cref="SessionOptions"/> (VLS-006 WS1) — the .NET twin of
/// speech-core's <c>SessionOptionsHook</c>. Runs once per session, after the device EP is applied and
/// before the session is built, so a model can install a custom EP or tune options without the host
/// needing to know about it.
/// </summary>
public delegate void OnnxSessionOptionsHook(SessionOptions options, string modelPath, OnnxDevice device);

/// <summary>Default <see cref="IOnnxSession"/> over a loaded ORT session.</summary>
internal sealed class LoadedOnnxSession : IOnnxSession
{
    public LoadedOnnxSession(InferenceSession session, OnnxDevice activeDevice)
    {
        Session = session;
        ActiveDevice = activeDevice;
        // ORT's InputNames/OutputNames are IReadOnlyList<string> in the model's declared index order —
        // unlike InputMetadata.Keys (a Dictionary key set, whose enumeration order isn't contractual). Copy
        // so the handle's lists don't depend on the session's lifetime.
        InputNames = session.InputNames.ToArray();
        OutputNames = session.OutputNames.ToArray();
    }

    public InferenceSession Session { get; }
    public IReadOnlyList<string> InputNames { get; }
    public IReadOnlyList<string> OutputNames { get; }
    public OnnxDevice ActiveDevice { get; }
}
