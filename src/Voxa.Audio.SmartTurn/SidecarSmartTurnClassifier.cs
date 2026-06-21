using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Voxa.Speech;

namespace Voxa.Audio.SmartTurn;

/// <summary>The smart-turn sidecar transport seam — a Python process in production, a fake in tests.</summary>
internal interface ISmartTurnSidecar : IAsyncDisposable, IDisposable
{
    Task StartAsync(CancellationToken ct);
    Task<double> PredictAsync(ReadOnlyMemory<byte> pcm, int sampleRate, CancellationToken ct);

    /// <summary>Kill the current process so the next <see cref="StartAsync"/> relaunches a clean one.</summary>
    void Reset();
}

/// <summary>
/// An <see cref="ISmartTurnClassifier"/> that runs the real turn-detection model in an out-of-process
/// Python sidecar — the same isolation <c>Voxa.Speech.Sidecar</c> uses for heavy TTS. The model's
/// preprocessing (Whisper feature extraction for <c>pipecat-ai/smart-turn-v3</c>) runs natively in
/// Python, so there's no fragile C# reimplementation to validate; this class just speaks a tiny stdio
/// protocol to it. Requests are serialized (turn detection is sequential), and any failure fails
/// "complete" so a sidecar problem degrades to classic silence behavior rather than stranding the turn.
/// </summary>
public sealed class SidecarSmartTurnClassifier : ISmartTurnClassifier, IDisposable, IAsyncDisposable
{
    private readonly SmartTurnOptions _options;
    private readonly ISmartTurnSidecar _sidecar;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _started;
    private bool _disposed;

    /// <summary>Create a classifier that launches the sidecar process described by <paramref name="options"/>.</summary>
    public SidecarSmartTurnClassifier(SmartTurnOptions options, ILogger? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger.Instance;
        _sidecar = new ProcessSmartTurnSidecar(options, _logger);
    }

    /// <summary>Test seam: drive a supplied sidecar instead of launching a process.</summary>
    internal SidecarSmartTurnClassifier(SmartTurnOptions options, ISmartTurnSidecar sidecar, ILogger? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _sidecar = sidecar ?? throw new ArgumentNullException(nameof(sidecar));
        _logger = logger ?? NullLogger.Instance;
    }

    public async ValueTask<bool> IsTurnCompleteAsync(ReadOnlyMemory<byte> recentSpeechPcm, int sampleRate, CancellationToken ct)
    {
        if (recentSpeechPcm.IsEmpty) return true;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Two budgets: a generous one for the first launch + model load (paid once), a tight one for
            // steady-state inference — so a hung/loading sidecar fails "complete" rather than stalling the
            // turn while the VAD waits on it. A timeout cancels a LINKED token, not the pipeline ct, so the
            // catch below treats it as a failure (fail-safe) and not a user interruption.
            if (!_started)
            {
                using var startCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                startCts.CancelAfter(_options.SidecarReadyTimeoutMs);
                await _sidecar.StartAsync(startCts.Token).ConfigureAwait(false);
                _started = true;
            }
            try
            {
                using var predictCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                predictCts.CancelAfter(_options.SidecarTimeoutMs);
                var probability = await _sidecar.PredictAsync(recentSpeechPcm, sampleRate, predictCts.Token).ConfigureAwait(false);
                return probability >= _options.Threshold;
            }
            catch
            {
                // Any abandonment of an in-flight prediction — timeout, crash, OR a user interruption — leaves
                // the request unanswered and a stale response queued, desyncing the process. Kill it so the
                // next turn relaunches clean instead of reading the previous turn's probability. The outer
                // handler then decides fail-safe-complete vs. propagating the interruption.
                _started = false;
                _sidecar.Reset();
                throw;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A crashed / erroring / timed-out sidecar must never hold the turn open — fail to "complete"
            // and let the next turn relaunch it (the process was reset above / StartAsync relaunches it).
            // A timeout is a "slow this turn / slow first-run load" event → Information; a real failure
            // (e.g. Python/deps missing, model load error, process exit) → Warning. No longer Debug, which
            // is below the typical Information floor (so it was invisible).
            _started = false;
            if (ex is OperationCanceledException)
                _logger.LogInformation("Smart-turn sidecar timed out; classic silence detection this turn.");
            else
                _logger.LogWarning(ex, "Smart-turn sidecar failed; falling back to classic silence detection (turn-complete).");
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Both disposal paths: a host container may tear this singleton down synchronously
    // (StudioServices.Reconfigure → ServiceProvider.Dispose) or asynchronously, and an
    // IAsyncDisposable-only service throws on the synchronous path.
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sidecar.Dispose();
        _gate.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _sidecar.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}

/// <summary>Launches the smart-turn sidecar as a child process and speaks the stdio protocol over it.</summary>
internal sealed class ProcessSmartTurnSidecar : ISmartTurnSidecar
{
    private readonly SmartTurnOptions _options;
    private readonly ILogger _logger;
    private Process? _process;

    public ProcessSmartTurnSidecar(SmartTurnOptions options, ILogger logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        if (_process is { HasExited: false }) return; // already running & ready
        DisposeProcess(); // a dead process — relaunch

        var (fileName, args) = ResolveLaunch(_options);
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,   // no flashing console window on Windows each time the sidecar (re)launches
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(_options.Model);

        try { _process = Process.Start(psi); }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to launch the Voxa smart-turn sidecar '{fileName}': {ex.Message}", ex);
        }
        if (_process is null)
            throw new InvalidOperationException($"Failed to launch the Voxa smart-turn sidecar '{fileName}'.");

        // Drain stderr to the log so a crash leaves a trail (never blocks the protocol on stdout).
        var process = _process;
        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync(CancellationToken.None).ConfigureAwait(false)) is not null)
                    _logger.LogDebug("voxa-smart-turn-sidecar: {Line}", line);
            }
            catch { /* best-effort diagnostics drain */ }
        }, CancellationToken.None);

        // Wait for the model to finish loading before the first prediction, so a slow first-run download is
        // a (caller-bounded) startup wait rather than silent latency folded into the first turn. A failure
        // here disposes the half-loaded process so the next turn relaunches cleanly.
        try
        {
            await SmartTurnSidecarProtocol.ReadReadyAsync(process.StandardOutput.BaseStream, ct).ConfigureAwait(false);
        }
        catch
        {
            DisposeProcess();
            throw;
        }
    }

    public async Task<double> PredictAsync(ReadOnlyMemory<byte> pcm, int sampleRate, CancellationToken ct)
    {
        if (_process is null || _process.HasExited)
            throw new InvalidOperationException("Voxa smart-turn sidecar is not running.");

        var stdin = _process.StandardInput.BaseStream;
        await stdin.WriteAsync(SmartTurnSidecarProtocol.EncodeRequestHeader(sampleRate, pcm.Length), ct).ConfigureAwait(false);
        await stdin.WriteAsync(pcm, ct).ConfigureAwait(false);
        await stdin.FlushAsync(ct).ConfigureAwait(false);

        return await SmartTurnSidecarProtocol.ReadProbabilityAsync(_process.StandardOutput.BaseStream, ct).ConfigureAwait(false);
    }

    private static (string FileName, IReadOnlyList<string> Args) ResolveLaunch(SmartTurnOptions o)
    {
        if (!string.IsNullOrEmpty(o.ExecutablePath)) return (o.ExecutablePath, []);
        if (!string.IsNullOrEmpty(o.PythonScript)) return (o.PythonExe, [ResolveScript(o.PythonScript)]);
        throw new InvalidOperationException(
            "No Voxa smart-turn sidecar is configured. Set Voxa:SmartTurn:PythonScript (with " +
            "Voxa:SmartTurn:PythonExe) to run sidecar/voxa_smart_turn_sidecar.py, or " +
            "Voxa:SmartTurn:ExecutablePath to a frozen binary. See the Voxa.Audio.SmartTurn README.");
    }

    // A relative script (the default "sidecar/voxa_smart_turn_sidecar.py", which the package copies next
    // to the host's output) resolves against the app base directory first, then falls back to the path as
    // given (cwd-relative) — so the bundled script works regardless of the process's working directory.
    private static string ResolveScript(string script)
    {
        if (Path.IsPathRooted(script)) return script;
        var local = Path.Combine(AppContext.BaseDirectory, script);
        return File.Exists(local) ? local : script;
    }

    private void DisposeProcess()
    {
        if (_process is null) return;
        try
        {
            if (!_process.HasExited) { _process.StandardInput.Close(); _process.Kill(entireProcessTree: true); }
        }
        catch { /* already gone */ }
        try { _process.Dispose(); } catch { }
        _process = null;
    }

    public void Reset() => DisposeProcess();

    public void Dispose() => DisposeProcess();

    public async ValueTask DisposeAsync()
    {
        var process = _process;
        DisposeProcess();
        if (process is not null)
        {
            try { await process.WaitForExitAsync().ConfigureAwait(false); } catch { /* best-effort */ }
        }
    }
}

/// <summary>
/// The Voxa ↔ smart-turn-sidecar stdio protocol. <b>Request:</b> one JSON header line
/// (<c>{"sample_rate":N,"bytes":M}</c>) then <c>M</c> bytes of 16-bit mono PCM. <b>Response:</b> one JSON
/// line — <c>{"probability":x}</c> (0..1) or <c>{"error":"…"}</c>. Tiny and framework-free so both ends
/// implement it trivially and the framing is unit-testable over a <see cref="MemoryStream"/>.
/// </summary>
internal static class SmartTurnSidecarProtocol
{
    public static byte[] EncodeRequestHeader(int sampleRate, int byteCount)
        => Encoding.UTF8.GetBytes($"{{\"sample_rate\":{sampleRate},\"bytes\":{byteCount}}}\n");

    public static async Task ReadReadyAsync(Stream stdout, CancellationToken ct)
    {
        var line = await ReadLineAsync(stdout, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Voxa smart-turn sidecar exited before signaling readiness.");

        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
                throw new InvalidOperationException($"Voxa smart-turn sidecar failed to start: {err.GetString()}");
            if (root.TryGetProperty("ready", out var r) && r.ValueKind == JsonValueKind.True)
                return;
        }
        throw new InvalidOperationException($"Voxa smart-turn sidecar sent an unexpected readiness line: {line}");
    }

    public static async Task<double> ReadProbabilityAsync(Stream stdout, CancellationToken ct)
    {
        var line = await ReadLineAsync(stdout, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Voxa smart-turn sidecar closed its output before responding.");

        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
                throw new InvalidOperationException($"Voxa smart-turn sidecar error: {err.GetString()}");
            if (root.TryGetProperty("probability", out var p) && p.ValueKind == JsonValueKind.Number)
                return p.GetDouble();
        }
        throw new InvalidOperationException($"Voxa smart-turn sidecar sent an unrecognized response: {line}");
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            var n = await stream.ReadAsync(one.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (n == 0) return sb.Length == 0 ? null : sb.ToString();
            if (one[0] == (byte)'\n') return sb.ToString();
            if (one[0] != (byte)'\r') sb.Append((char)one[0]); // header is ASCII JSON
        }
    }
}
