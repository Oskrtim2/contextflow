using System.Text;

namespace ContextFlow.Infrastructure;

/// <summary>
/// Small lock-protected rolling file logger. Deliberately dependency-free so
/// the app remains fully offline and self-contained.
/// </summary>
public sealed class AppLog : IDisposable
{
    private const long MaxLogBytes = 2 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly string _logFile;
    private bool _disposed;

    public AppLog(AppPaths paths)
    {
        _logFile = paths.LogFile;
        try
        {
            paths.EnsureCreated();
            RotateIfNeeded();
        }
        catch
        {
            // Logging must never prevent the app from starting.
        }
    }

    public void Info(string message) => Write("INFO", message, null);
    public void Warn(string message) => Write("WARN", message, null);
    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    public void Write(string level, string message, Exception? exception)
    {
        if (_disposed)
        {
            return;
        }

        var builder = new StringBuilder();
        builder.Append(DateTimeOffset.Now.ToString("O"))
            .Append(" [")
            .Append(level)
            .Append("] ")
            .Append(message);

        if (exception is not null)
        {
            builder.AppendLine()
                .Append(exception);
        }

        builder.AppendLine();

        lock (_gate)
        {
            try
            {
                RotateIfNeeded();
                File.AppendAllText(_logFile, builder.ToString(), Encoding.UTF8);
            }
            catch
            {
                // Ignore transient log I/O failures.
            }
        }
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(_logFile))
        {
            return;
        }

        var info = new FileInfo(_logFile);
        if (info.Length < MaxLogBytes)
        {
            return;
        }

        var previous = _logFile + ".1";
        if (File.Exists(previous))
        {
            File.Delete(previous);
        }

        File.Move(_logFile, previous);
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
