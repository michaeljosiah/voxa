using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Voxa.Speech.Piper;

/// <summary>
/// Process-wide registry of warm piper hosts, keyed by (executable, voice, length-scale). Hosts
/// outlive connections — engine instances are per-connection, the pool is not — and die with the
/// app (ProcessExit hook; hosts also kill their children on dispose, so no orphan piper.exe).
/// Up to <c>MaxProcesses</c> hosts per key so two simultaneous syntheses don't serialize behind
/// one process.
/// </summary>
internal sealed class PiperProcessPool : IDisposable
{
    private static readonly ConcurrentDictionary<string, PiperProcessPool> Pools = new();

    static PiperProcessPool()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DisposeAll();
    }

    public static PiperProcessPool GetOrCreate(
        string exePath, string modelPath, double lengthScale, int maxProcesses, ILogger? logger)
    {
        var key = $"{exePath}|{modelPath}|{lengthScale}|{maxProcesses}";
        return Pools.GetOrAdd(key, _ => new PiperProcessPool(
            exePath, modelPath, lengthScale, Math.Max(1, maxProcesses), logger ?? NullLogger.Instance));
    }

    /// <summary>Kill every pooled piper process. Called on process exit and from tests.</summary>
    public static void DisposeAll()
    {
        foreach (var key in Pools.Keys.ToArray())
        {
            if (Pools.TryRemove(key, out var pool)) pool.Dispose();
        }
    }

    private readonly string _exePath;
    private readonly string _modelPath;
    private readonly double _lengthScale;
    private readonly int _maxProcesses;
    private readonly ILogger _logger;
    private readonly List<PiperProcessHost> _hosts = new();
    private readonly object _gate = new();
    private int _roundRobin;
    private bool _disposed;

    private PiperProcessPool(string exePath, string modelPath, double lengthScale, int maxProcesses, ILogger logger)
    {
        _exePath = exePath;
        _modelPath = modelPath;
        _lengthScale = lengthScale;
        _maxProcesses = maxProcesses;
        _logger = logger;
    }

    public Task<byte[]> SynthesizeAsync(string text, CancellationToken ct)
        => SelectHost().SynthesizeAsync(text, ct);

    private PiperProcessHost SelectHost()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Prefer an idle host; grow up to the cap when all are busy; then round-robin
            // (the host's internal lease provides the actual queuing).
            var idle = _hosts.FirstOrDefault(h => !h.IsBusy);
            if (idle is not null) return idle;

            if (_hosts.Count < _maxProcesses)
            {
                var host = new PiperProcessHost(_exePath, _modelPath, _lengthScale, _logger);
                _hosts.Add(host);
                return host;
            }

            return _hosts[Math.Abs(_roundRobin++ % _hosts.Count)];
        }
    }

    /// <summary>Live child pids, for orphan-check tests.</summary>
    internal IReadOnlyList<int> LiveProcessIds()
    {
        lock (_gate) return _hosts.Select(h => h.ProcessId).Where(p => p.HasValue).Select(p => p!.Value).ToList();
    }

    /// <summary>
    /// Live child pids across every pool, for orphan-check tests. Captures this run's processes
    /// by id so the assertion doesn't race a machine-global process name against other test
    /// assemblies running their own piper concurrently.
    /// </summary>
    internal static IReadOnlyList<int> AllLiveProcessIds()
        => Pools.Values.SelectMany(p => p.LiveProcessIds()).ToList();

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var host in _hosts) host.Dispose();
            _hosts.Clear();
        }
    }
}
