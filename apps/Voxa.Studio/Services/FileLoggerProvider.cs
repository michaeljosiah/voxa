using System.Text;
using Microsoft.Extensions.Logging;

namespace Voxa.Studio.Services;

/// <summary>
/// A tiny file logger so Studio — a GUI app with no console — actually persists what its components log.
/// Without it the framework <c>AddDebug()</c> sink only reaches an attached debugger, so a failure like the
/// smart-turn sidecar crashing on a missing Python dependency would vanish. Writes timestamped lines to
/// <see cref="LogPath"/>; thread-safe; best-effort (logging never throws into the app). Captures
/// Information+ so the file carries positive signals too (e.g. "Smart turn: held open (95 ms)"), not just
/// failures — which is how you confirm smart turn is actually doing something.
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    /// <summary>%LOCALAPPDATA%\Voxa\Studio\studio.log (platform-appropriate elsewhere).</summary>
    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Voxa", "Studio", "studio.log");

    private readonly LogLevel _min;
    private readonly object _gate = new();

    public FileLoggerProvider(LogLevel min)
    {
        _min = min;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            // Bound growth: start fresh past ~5 MB, otherwise append across runs (a Config Apply rebuilds
            // the container, so truncating on every construct would wipe the running session's log).
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 5 * 1024 * 1024)
                File.Delete(LogPath);
            File.AppendAllText(LogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} === Voxa Studio session start ==={Environment.NewLine}");
        }
        catch { /* logging must never crash the app */ }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose() { }

    private void Write(LogLevel level, string category, string message, Exception? ex)
    {
        var sb = new StringBuilder()
            .Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append(" [").Append(level).Append("] ")
            .Append(category).Append(": ").Append(message);
        if (ex is not null) sb.Append(Environment.NewLine).Append(ex);
        sb.Append(Environment.NewLine);
        lock (_gate)
        {
            try { File.AppendAllText(LogPath, sb.ToString()); }
            catch { /* best-effort */ }
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= provider._min && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            provider.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}
