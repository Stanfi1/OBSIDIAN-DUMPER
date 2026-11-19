using System.Collections.ObjectModel;

namespace Obsidian.Dumper.Core.Diagnostics;

public enum LogLevel
{
    Trace = 0,
    Info,
    Success,
    Warning,
    Error,
}

public sealed record LogEntry(DateTime Timestamp, LogLevel Level, string Message, string? Scope = null)
{
    public string TimeLabel => Timestamp.ToString("HH:mm:ss.fff");
    public string LevelLabel => Level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Info => "INFO",
        LogLevel.Success => "OK",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "FAIL",
        _ => "INFO",
    };

    public override string ToString() => $"{TimeLabel} {LevelLabel,-5} {(Scope is null ? "" : Scope + ": ")}{Message}";
}

public interface ILogSink
{
    void Write(LogLevel level, string message, string? scope = null);
}

/// <summary>
/// Central log bus. The engine only knows this interface; the UI subscribes and
/// mirrors entries into its observable console, the CLI prints them.
/// </summary>
public sealed class LogBus : ILogSink
{
    private readonly object _gate = new();
    private readonly List<LogEntry> _history = new(512);
    private readonly int _capacity;

    public LogBus(int capacity = 2000)
    {
        _capacity = Math.Max(64, capacity);
    }

    public event Action<LogEntry>? EntryWritten;

    public void Write(LogLevel level, string message, string? scope = null)
    {
        var entry = new LogEntry(DateTime.Now, level, message, scope);
        lock (_gate)
        {
            _history.Add(entry);
            if (_history.Count > _capacity)
                _history.RemoveRange(0, _history.Count - _capacity);
        }

        Action<LogEntry>? handlers = EntryWritten;
        if (handlers == null)
            return;

        // A faulty subscriber (e.g. a UI bridge that throws during shutdown) must
        // never break the engine operation that is reporting progress.
        foreach (Delegate subscriber in handlers.GetInvocationList())
        {
            try
            {
                ((Action<LogEntry>)subscriber)(entry);
            }
            catch (Exception)
            {
                // Logging is best effort; never propagate subscriber failures.
            }
        }
    }

    public void Trace(string message, string? scope = null) => Write(LogLevel.Trace, message, scope);
    public void Info(string message, string? scope = null) => Write(LogLevel.Info, message, scope);
    public void Success(string message, string? scope = null) => Write(LogLevel.Success, message, scope);
    public void Warn(string message, string? scope = null) => Write(LogLevel.Warning, message, scope);
    public void Error(string message, string? scope = null) => Write(LogLevel.Error, message, scope);

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate)
        {
            return _history.ToArray();
        }
    }

    public ObservableCollection<LogEntry> CreateObservableMirror()
    {
        var mirror = new ObservableCollection<LogEntry>();
        foreach (var entry in Snapshot())
            mirror.Add(entry);
        EntryWritten += entry => mirror.Add(entry);
        return mirror;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _history.Clear();
        }
    }
}

/// <summary>Silent sink used by tests and headless primitives.</summary>
public sealed class NullLogSink : ILogSink
{
    public static NullLogSink Instance { get; } = new();
    public void Write(LogLevel level, string message, string? scope = null) { }
}
