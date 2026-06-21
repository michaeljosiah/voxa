using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Voxa.Speech.Sidecar;

/// <summary>The sidecar transport seam — a process in production, a fake in tests.</summary>
internal interface ISidecarChannel : IAsyncDisposable
{
    Task StartAsync(CancellationToken ct);
    IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(SidecarRequest request, CancellationToken ct);
}

/// <summary>
/// Expressive / multilingual / cloning TTS over an out-of-process sidecar (VVL-002), exposed to the
/// pipeline as an ordinary <see cref="ITextToSpeechEngine"/>. The heavy model lives in the sidecar
/// process (the same isolation Piper uses for espeak-ng); this class just speaks the
/// <see cref="SidecarProtocol"/> to it. One utterance is in flight at a time (the sidecar streams
/// sequentially), so synthesis is serialized by a semaphore.
/// </summary>
public sealed class SidecarTtsEngine : ITextToSpeechEngine
{
    private readonly SidecarOptions _options;
    private readonly ISidecarChannel _channel;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _started;

    /// <summary>Create an engine that launches the sidecar process described by <paramref name="options"/>.</summary>
    public SidecarTtsEngine(SidecarOptions options, ILogger? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _channel = new ProcessSidecarChannel(options, logger ?? NullLogger.Instance);
    }

    /// <summary>Test seam: drive a supplied channel instead of launching a process.</summary>
    internal SidecarTtsEngine(SidecarOptions options, ISidecarChannel channel)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
    }

    public async Task StartAsync(CancellationToken ct)
    {
        if (_started) return;
        await _channel.StartAsync(ct).ConfigureAwait(false);
        _started = true;
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(
        string text, [EnumeratorCancellation] CancellationToken ct)
    {
        var request = new SidecarRequest(text, _options.Voice, _options.Language, _options.OutputSampleRate);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await foreach (var chunk in _channel.SynthesizeAsync(request, ct).ConfigureAwait(false))
                yield return chunk;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _channel.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}

/// <summary>Launches the sidecar as a child process and speaks <see cref="SidecarProtocol"/> over its stdio.</summary>
internal sealed class ProcessSidecarChannel : ISidecarChannel
{
    private readonly SidecarOptions _options;
    private readonly ILogger _logger;
    private Process? _process;

    public ProcessSidecarChannel(SidecarOptions options, ILogger logger)
    {
        _options = options;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        var (fileName, args) = ResolveLaunch(_options);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,   // no flashing console window on Windows when the TTS sidecar launches
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(_options.Model);
        psi.ArgumentList.Add("--sample-rate");
        psi.ArgumentList.Add(_options.OutputSampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture));

        try
        {
            _process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            throw new VoxaModelUnavailableException(
                $"Failed to launch the Voxa TTS sidecar '{fileName}': {ex.Message}", ex);
        }
        if (_process is null)
            throw new VoxaModelUnavailableException($"Failed to launch the Voxa TTS sidecar '{fileName}'.");

        // Drain stderr to the log so a crash leaves a trail (never blocks the protocol on stdout).
        var process = _process;
        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
                    _logger.LogDebug("voxa-tts-sidecar: {Line}", line);
            }
            catch { /* best-effort diagnostics drain */ }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(
        SidecarRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        if (_process is null)
            throw new VoxaModelUnavailableException("Voxa TTS sidecar has not been started.");

        var bytes = SidecarProtocol.EncodeRequest(request);
        await _process.StandardInput.BaseStream.WriteAsync(bytes.AsMemory(), ct).ConfigureAwait(false);
        await _process.StandardInput.BaseStream.FlushAsync(ct).ConfigureAwait(false);

        await foreach (var chunk in SidecarProtocol.ReadResponseAsync(_process.StandardOutput.BaseStream, ct).ConfigureAwait(false))
            yield return chunk;
    }

    private static (string FileName, IReadOnlyList<string> Args) ResolveLaunch(SidecarOptions o)
    {
        if (!string.IsNullOrEmpty(o.ExecutablePath))
            return (o.ExecutablePath, []);
        if (!string.IsNullOrEmpty(o.PythonScript))
            return (o.PythonExe, [o.PythonScript]);

        throw new VoxaModelUnavailableException(
            "No Voxa TTS sidecar is configured. VVL-002 ships no pinned frozen binary yet, so set " +
            "Voxa:Sidecar:ExecutablePath to a sidecar binary you built, or Voxa:Sidecar:PythonScript " +
            "(with Voxa:Sidecar:PythonExe) to run sidecar/voxa_tts_sidecar.py in development. " +
            "See the Voxa.Speech.Sidecar README.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is null) return;
        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.Close();
                _process.Kill(entireProcessTree: true);
            }
        }
        catch { /* already gone */ }

        try { await _process.WaitForExitAsync().ConfigureAwait(false); }
        catch { /* best-effort */ }

        _process.Dispose();
        _process = null;
    }
}
