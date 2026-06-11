using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Voxa.Speech.Piper;

/// <summary>
/// One warm piper child process (VLS-001 WS2.2). Spawn-per-utterance would pay the ~0.3–1.5 s
/// voice load per sentence; instead the process stays alive in <c>--json-input</c> mode and each
/// request writes one JSON line with a per-utterance <c>output_file</c>.
///
/// <para>
/// Completion detection is layered: (1) piper prints the output path to stdout when an utterance
/// is done (verified against the real 2023.11.14-2 binaries); (2) the WAV's RIFF size field
/// accounts for the whole file; (3) the file size is stable across several polls. Any layer
/// completes the wait, so a platform with block-buffered stdout still works.
/// </para>
///
/// <para>
/// One request in flight per process (piper reads stdin sequentially);
/// <see cref="PiperProcessPool"/> runs several hosts for cross-connection throughput. A crashed
/// process faults the pending request with the captured stderr tail and restarts lazily on the
/// next request.
/// </para>
/// </summary>
internal sealed class PiperProcessHost : IDisposable
{
    private static readonly TimeSpan SynthesisTimeout = TimeSpan.FromSeconds(60);

    private readonly string _exePath;
    private readonly string _modelPath;
    private readonly double _lengthScale;
    private readonly ILogger _logger;
    private readonly string _outputDir;
    private readonly SemaphoreSlim _lease = new(1, 1);
    private readonly ConcurrentQueue<string> _stderrTail = new();

    private Process? _process;
    private StreamWriter? _stdin;
    private volatile StdoutWaiter? _waiter;
    private long _seq;
    private bool _disposed;

    private sealed record StdoutWaiter(string ExpectedFileName, TaskCompletionSource Signal);

    public PiperProcessHost(string exePath, string modelPath, double lengthScale, ILogger logger)
    {
        _exePath = exePath;
        _modelPath = modelPath;
        _lengthScale = lengthScale;
        _logger = logger;
        _outputDir = Path.Combine(Path.GetTempPath(), "voxa-piper", Guid.NewGuid().ToString("N"));
    }

    /// <summary>True while a synthesis holds the lease — used by the pool for host selection.</summary>
    public bool IsBusy => _lease.CurrentCount == 0;

    public async Task<byte[]> SynthesizeAsync(string text, CancellationToken ct)
    {
        await _lease.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureProcess();

            var outFile = Path.Combine(_outputDir, $"u-{Interlocked.Increment(ref _seq)}.wav");
            var waiter = new StdoutWaiter(
                Path.GetFileName(outFile),
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            _waiter = waiter;

            try
            {
                // piper accepts forward slashes everywhere and echoes the path verbatim.
                var json = JsonSerializer.Serialize(new { text, output_file = outFile.Replace('\\', '/') });
                await _stdin!.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
                await _stdin.FlushAsync(ct).ConfigureAwait(false);

                await AwaitCompletionAsync(outFile, waiter.Signal.Task, ct).ConfigureAwait(false);

                var bytes = await File.ReadAllBytesAsync(outFile, ct).ConfigureAwait(false);
                return bytes;
            }
            finally
            {
                _waiter = null;
                try { if (File.Exists(outFile)) File.Delete(outFile); } catch { /* best-effort */ }
            }
        }
        finally
        {
            _lease.Release();
        }
    }

    private async Task AwaitCompletionAsync(string outFile, Task stdoutSignal, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + SynthesisTimeout;
        long lastSize = -1;
        int stablePolls = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // Layer 1: piper acknowledged on stdout — authoritative.
            if (stdoutSignal.IsCompleted) return;

            // Crash: fault with the stderr tail and let the next request restart the process.
            var process = _process;
            if (process is null || process.HasExited)
            {
                var exitCode = process?.ExitCode;
                DropProcess();
                throw new InvalidOperationException(
                    $"piper exited unexpectedly (exit code {exitCode?.ToString() ?? "unknown"}) while synthesizing. " +
                    $"stderr tail:{Environment.NewLine}{StderrTail()}");
            }

            // Layer 2: the WAV's RIFF size accounts for the whole file.
            if (WavAudio.FileIsComplete(outFile)) return;

            // Layer 3: file present and size stable for ~100 ms.
            if (File.Exists(outFile))
            {
                var size = new FileInfo(outFile).Length;
                stablePolls = size > 44 && size == lastSize ? stablePolls + 1 : 0;
                lastSize = size;
                if (stablePolls >= 4) return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"piper did not produce output within {SynthesisTimeout.TotalSeconds:F0}s. " +
                    $"stderr tail:{Environment.NewLine}{StderrTail()}");
            }

            await Task.Delay(25, ct).ConfigureAwait(false);
        }
    }

    private void EnsureProcess()
    {
        if (_process is { HasExited: false }) return;
        DropProcess();

        Directory.CreateDirectory(_outputDir);
        var psi = new ProcessStartInfo
        {
            FileName = _exePath,
            WorkingDirectory = _outputDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(_modelPath);
        psi.ArgumentList.Add("--json-input");
        if (Math.Abs(_lengthScale - 1.0) > double.Epsilon)
        {
            psi.ArgumentList.Add("--length_scale");
            psi.ArgumentList.Add(_lengthScale.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start piper at '{_exePath}'.");
        _logger.LogInformation("piper host started (pid {Pid}, voice {Voice})",
            process.Id, Path.GetFileName(_modelPath));

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            var waiter = _waiter;
            if (waiter is not null && e.Data.Contains(waiter.ExpectedFileName, StringComparison.OrdinalIgnoreCase))
                waiter.Signal.TrySetResult();
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            _stderrTail.Enqueue(e.Data);
            while (_stderrTail.Count > 50) _stderrTail.TryDequeue(out string? _);
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        _process = process;
        _stdin = process.StandardInput;
    }

    private void DropProcess()
    {
        var process = _process;
        _process = null;
        _stdin = null;
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* already gone */ }
        process.Dispose();
    }

    private string StderrTail() => string.Join(Environment.NewLine, _stderrTail);

    /// <summary>Pid of the live child, for orphan-check tests. Null when not running.</summary>
    internal int? ProcessId => _process is { HasExited: false } p ? p.Id : null;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DropProcess();
        try { if (Directory.Exists(_outputDir)) Directory.Delete(_outputDir, recursive: true); } catch { }
        _lease.Dispose();
    }
}
