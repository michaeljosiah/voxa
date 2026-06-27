using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Voxa.Speech.Voxtral;

/// <summary>The realtime-server seam — a managed child process (or a connect-only resolver) in production, a fake in
/// tests. <see cref="StartAsync"/> ensures the server is ready and returns the ws endpoint the engine connects to.</summary>
internal interface IVoxtralServer : IAsyncDisposable
{
    /// <summary>Ensure the vLLM realtime server is running and ready; return the <c>/v1/realtime</c> ws endpoint.</summary>
    Task<Uri> StartAsync(CancellationToken ct);
}

/// <summary>
/// Owns the local vLLM realtime server for <see cref="VoxtralRealtimeSttEngine"/> (VLS-009), mirroring the VVL-002
/// <c>ProcessSidecarChannel</c>: in <b>managed</b> mode it launches the configured server, drains its logs, polls the
/// <c>/health</c> route until ready (a cold 4B load is slow), and kills the process tree on dispose. In
/// <b>connect-only</b> mode (<see cref="VoxtralOptions.ServerUrl"/>) it launches nothing and just hands back the
/// endpoint — disposing it never touches a server Voxa didn't start.
/// </summary>
internal sealed class VoxtralServerProcess : IVoxtralServer
{
    private readonly VoxtralOptions _options;
    private readonly ILogger _logger;
    private Process? _process;

    public VoxtralServerProcess(VoxtralOptions options, ILogger logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<Uri> StartAsync(CancellationToken ct)
    {
        var endpoint = _options.ResolveEndpoint();

        // Connect-only: the user runs vLLM themselves. Don't launch or health-poll — a ConnectAsync against a server
        // that isn't up fails loudly at session start, which is the right signal for "your server isn't running".
        if (!_options.HasManagedLaunch)
            return endpoint;

        Launch();
        await WaitForReadyAsync(ct).ConfigureAwait(false);
        return endpoint;
    }

    private void Launch()
    {
        var (fileName, args) = ResolveLaunch(_options);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true, // no flashing console window on Windows when the server launches
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            _process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            throw new VoxaModelUnavailableException(
                $"Failed to launch the Voxtral vLLM server '{fileName}': {ex.Message}", ex);
        }
        if (_process is null)
            throw new VoxaModelUnavailableException($"Failed to launch the Voxtral vLLM server '{fileName}'.");

        // Drain both streams to the log so a startup crash leaves a trail (vLLM logs to stderr).
        DrainToLog(_process.StandardError, "stderr");
        DrainToLog(_process.StandardOutput, "stdout");
    }

    private void DrainToLog(StreamReader reader, string label)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
                    _logger.LogDebug("voxtral-server {Label}: {Line}", label, line);
            }
            catch { /* best-effort diagnostics drain */ }
        }, CancellationToken.None);
    }

    private async Task WaitForReadyAsync(CancellationToken ct)
    {
        var health = _options.ResolveHealthEndpoint();
        var deadline = Environment.TickCount64 + _options.ReadyTimeoutSeconds * 1000L;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (_process is { HasExited: true } exited)
                throw new VoxaModelUnavailableException(
                    $"The Voxtral vLLM server exited during startup (exit code {exited.ExitCode}). Check the server logs.");

            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(TimeSpan.FromSeconds(2));
                using var resp = await VoxaHttp.Shared.GetAsync(health, attempt.Token).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException) { /* not listening yet */ }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* per-attempt timeout */ }

            if (Environment.TickCount64 > deadline)
                throw new VoxaModelUnavailableException(
                    $"The Voxtral vLLM server at {health.Authority} was not ready within {_options.ReadyTimeoutSeconds}s. " +
                    "Increase Voxa:Voxtral:ReadyTimeoutSeconds for a cold model load, or check the server logs.");

            await Task.Delay(500, ct).ConfigureAwait(false);
        }
    }

    private static (string FileName, IReadOnlyList<string> Args) ResolveLaunch(VoxtralOptions o)
    {
        if (!string.IsNullOrEmpty(o.ExecutablePath))
            return (o.ExecutablePath, o.LaunchArgs);
        if (!string.IsNullOrEmpty(o.LaunchCommand))
            return (o.LaunchCommand, o.LaunchArgs);

        // Unreachable: StartAsync only launches when HasManagedLaunch is true. Kept as a guard.
        throw new VoxaModelUnavailableException(
            "No Voxtral server hosting mode is configured. Set Voxa:Voxtral:ServerUrl to a running vLLM realtime " +
            "server, or Voxa:Voxtral:LaunchCommand/ExecutablePath to have Voxa start one. See the Voxa.Speech.Voxtral README.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is null) return; // connect-only, or never launched
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch { /* already gone */ }

        try { await _process.WaitForExitAsync().ConfigureAwait(false); }
        catch { /* best-effort */ }

        _process.Dispose();
        _process = null;
    }
}
