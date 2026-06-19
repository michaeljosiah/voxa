using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Voxa.Speech;

namespace Voxa.Audio.SmartTurn;

/// <summary>The smart-turn sidecar transport seam — a Python process in production, a fake in tests.</summary>
internal interface ISmartTurnSidecar : IAsyncDisposable
{
    Task StartAsync(CancellationToken ct);
    Task<double> PredictAsync(ReadOnlyMemory<byte> pcm, int sampleRate, CancellationToken ct);
}

/// <summary>
/// An <see cref="ISmartTurnClassifier"/> that runs the real turn-detection model in an out-of-process
/// Python sidecar — the same isolation <c>Voxa.Speech.Sidecar</c> uses for heavy TTS. The model's
/// preprocessing (Whisper feature extraction for <c>pipecat-ai/smart-turn-v3</c>) runs natively in
/// Python, so there's no fragile C# reimplementation to validate; this class just speaks a tiny stdio
/// protocol to it. Requests are serialized (turn detection is sequential), and any failure fails
/// "complete" so a sidecar problem degrades to classic silence behavior rather than stranding the turn.
/// </summary>
public sealed class SidecarSmartTurnClassifier : ISmartTurnClassifier
{
    private readonly SmartTurnOptions _options;
    private readonly ISmartTurnSidecar _sidecar;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _started;

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
            if (!_started)
            {
                await _sidecar.StartAsync(ct).ConfigureAwait(false);
                _started = true;
            }
            var probability = await _sidecar.PredictAsync(recentSpeechPcm, sampleRate, ct).ConfigureAwait(false);
            return probability >= _options.Threshold;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A crashed/erroring sidecar must never hold the turn open — fail to "complete" and let the
            // next turn relaunch it (ProcessSmartTurnSidecar.StartAsync relaunches a dead process).
            _started = false;
            _logger.LogDebug(ex, "Smart-turn sidecar failed; defaulting to turn-complete.");
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
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

    public Task StartAsync(CancellationToken ct)
    {
        if (_process is { HasExited: false }) return Task.CompletedTask; // already running
        DisposeProcess(); // a dead process — relaunch

        var (fileName, args) = ResolveLaunch(_options);
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
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

        return Task.CompletedTask;
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
        if (!string.IsNullOrEmpty(o.PythonScript)) return (o.PythonExe, [o.PythonScript]);
        throw new InvalidOperationException(
            "No Voxa smart-turn sidecar is configured. Set Voxa:SmartTurn:PythonScript (with " +
            "Voxa:SmartTurn:PythonExe) to run sidecar/voxa_smart_turn_sidecar.py, or " +
            "Voxa:SmartTurn:ExecutablePath to a frozen binary. See the Voxa.Audio.SmartTurn README.");
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
