namespace PCleaner.Core.Logging;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
}

public sealed record LogEntry(DateTime TimestampLocal, LogLevel Level, string Message)
{
    public override string ToString() => $"{TimestampLocal:yyyy-MM-dd HH:mm:ss.fff} [{Level,-7}] {Message}";
}

/// <summary>Minimal logging abstraction so the core library does not depend on a logging framework.</summary>
public interface ICleanerLog
{
    void Log(LogLevel level, string message);
}

public static class CleanerLogExtensions
{
    public static void Debug(this ICleanerLog log, string message) => log.Log(LogLevel.Debug, message);

    public static void Info(this ICleanerLog log, string message) => log.Log(LogLevel.Info, message);

    public static void Warn(this ICleanerLog log, string message) => log.Log(LogLevel.Warning, message);

    public static void Error(this ICleanerLog log, string message) => log.Log(LogLevel.Error, message);

    public static void Error(this ICleanerLog log, string message, Exception exception)
        => log.Log(LogLevel.Error, $"{message} ({exception.GetType().Name}: {exception.Message})");
}

/// <summary>Discards everything. Used by unit tests and as a default.</summary>
public sealed class NullLog : ICleanerLog
{
    public static NullLog Instance { get; } = new();

    public void Log(LogLevel level, string message)
    {
    }
}

/// <summary>Fans out to several sinks.</summary>
public sealed class CompositeLog : ICleanerLog
{
    private readonly ICleanerLog[] _sinks;

    public CompositeLog(params ICleanerLog[] sinks)
    {
        _sinks = sinks ?? [];
    }

    public void Log(LogLevel level, string message)
    {
        foreach (var sink in _sinks)
        {
            sink.Log(level, message);
        }
    }
}

/// <summary>
/// Appends log lines to a daily file below <c>%LOCALAPPDATA%\PCleaner\Logs</c>. Thread-safe; never throws.
/// </summary>
public sealed class FileLog : ICleanerLog, IDisposable
{
    private readonly object _gate = new();
    private readonly string _directory;
    private StreamWriter? _writer;
    private DateTime _writerDate;

    public FileLog(string? directory = null)
    {
        _directory = directory ?? GetDefaultDirectory();
    }

    public static string GetDefaultDirectory()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCleaner", "Logs");

    public string Directory => _directory;

    public void Log(LogLevel level, string message)
    {
        try
        {
            lock (_gate)
            {
                var now = DateTime.Now;
                if (_writer is null || _writerDate != now.Date)
                {
                    _writer?.Dispose();
                    System.IO.Directory.CreateDirectory(_directory);
                    var file = Path.Combine(_directory, $"PCleaner-{now:yyyy-MM-dd}.log");
                    _writer = new StreamWriter(file, append: true) { AutoFlush = true };
                    _writerDate = now.Date;
                }

                _writer.WriteLine(new LogEntry(now, level, message).ToString());
            }
        }
        catch
        {
            // Logging must never break a cleanup.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}

/// <summary>Keeps the most recent entries in memory and raises an event for UI consumption.</summary>
public sealed class MemoryLog : ICleanerLog
{
    private readonly object _gate = new();
    private readonly Queue<LogEntry> _entries = new();
    private readonly int _capacity;

    public MemoryLog(int capacity = 5000)
    {
        _capacity = Math.Max(100, capacity);
    }

    public event Action<LogEntry>? EntryAdded;

    public void Log(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);
        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > _capacity)
            {
                _entries.Dequeue();
            }
        }

        EntryAdded?.Invoke(entry);
    }

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate)
        {
            return [.. _entries];
        }
    }
}